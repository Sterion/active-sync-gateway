using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   Email content store + mail side-operations over IMAP/SMTP.
///   Item keys are IMAP UIDs (per folder); revisions encode the sync-relevant flags.
/// </summary>
public sealed partial class ImapMailBackend(
	ImapSession session,
	SmtpOptions smtpOptions,
	BackendCredentials credentials,
	string? mailAddress,
	Func<string, ImapIdleWatcher?> idleWatcherProvider,
	ILogger logger,
	ILogger? smtpWireLogger = null) : IContentStore, IMailOperations
{
	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private static readonly string[] SentNames = ["Sent", "Sent Items", "Sent Messages", "INBOX.Sent"];
	private static readonly string[] TrashNames = ["Trash", "Deleted Items", "Deleted Messages", "INBOX.Trash"];
	private static readonly string[] DraftsNames = ["Drafts", "INBOX.Drafts"];
	private static readonly string[] JunkNames = ["Junk", "Spam", "Junk E-mail", "INBOX.Junk"];

	public string EasClass => Protocol.EasClass.Email;

	// ---------- IContentStore ----------

	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyList<BackendFolder>>(async client =>
		{
			List<BackendFolder> result = new();
			IMailFolder? personal = client.PersonalNamespaces.Count > 0
				? client.GetFolder(client.PersonalNamespaces[0])
				: null;
			IList<IMailFolder> folders = personal is not null
				? await personal.GetSubfoldersAsync(false, ct).ConfigureAwait(false)
				: [];

			List<IMailFolder> all = new();

			async Task Walk(IMailFolder folder)
			{
				all.Add(folder);
				foreach (IMailFolder child in await folder.GetSubfoldersAsync(false, ct).ConfigureAwait(false))
					await Walk(child).ConfigureAwait(false);
			}

			foreach (IMailFolder f in folders)
				await Walk(f).ConfigureAwait(false);
			if (!all.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)))
				all.Insert(0, client.Inbox);

			foreach (IMailFolder folder in all)
			{
				if (folder.Attributes.HasFlag(FolderAttributes.NonExistent))
					continue;
				int type = ClassifyFolder(folder);
				string? parentKey = folder.ParentFolder is { } parent && !string.IsNullOrEmpty(parent.FullName)
					? ImapSession.ToBackendKey(parent.FullName)
					: null;
				result.Add(new BackendFolder(
					ImapSession.ToBackendKey(folder.FullName),
					folder.Name,
					parentKey,
					type,
					Protocol.EasClass.Email));
			}

			return result;
		}, ct);
	}

	public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyDictionary<string, string>>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			// A folder that stays selected between calls keeps a FROZEN view: servers
			// only announce newly delivered messages (EXISTS) when given the chance
			// (RFC 3501 NOOP/IDLE), and re-opening an already-open folder is a no-op in
			// MailKit. Without this NOOP, a SEARCH on the shared session can miss mail
			// delivered after the original SELECT indefinitely (observed on Stalwart).
			await client.NoOpAsync(ct).ConfigureAwait(false);
			SearchQuery query = filter.SinceUtc is { } since
				? SearchQuery.DeliveredAfter(since.Date)
				: SearchQuery.All;
			IList<UniqueId> uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
			if (uids.Count == 0)
				return new Dictionary<string, string>();
			IList<IMessageSummary> summaries = await folder
				.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			Dictionary<string, string> map = new(summaries.Count, StringComparer.Ordinal);
			foreach (IMessageSummary summary in summaries)
				map[summary.UniqueId.Id.ToString()] = RevisionOf(summary.Flags ?? MessageFlags.None);
			return map;
		}, ct);
	}

	public Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		return session.RunAsync<BackendItem?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(itemKey);
			IList<IMessageSummary> summaries = await folder
				.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			if (summaries.Count == 0)
				return null;
			MimeMessage message;
			try
			{
				message = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
			}
			catch (MessageNotFoundException)
			{
				return null;
			}

			MessageFlags flags = summaries[0].Flags ?? MessageFlags.None;
			MailConverter.MessageFlags converterFlags = new(
				(flags & MessageFlags.Seen) != 0,
				(flags & MessageFlags.Flagged) != 0,
				(flags & MessageFlags.Answered) != 0,
				summaries[0].Keywords?.Contains("$Forwarded") == true);
			List<XElement> data = MailConverter.ToApplicationData(
				message, converterFlags, bodyPreference,
				idx => MakeFileReference(folderBackendKey, itemKey, idx));
			return new BackendItem(data);
		}, ct);
	}

	public Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct)
	{
		// EAS 16.x drafts: the only mail class a client may create via Sync. Anything but
		// the Drafts folder keeps the historical refusal (per-item Status 6 upstream).
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			if (!IsDraftsFolder(folder))
				throw new BackendException("Creating mail items via Sync is only supported in the Drafts folder.");

			MimeMessage draft = DraftMessageBuilder.Build(applicationData, null, mailAddress);
			UniqueId? uid = await folder.AppendAsync(draft, MessageFlags.Draft, ct).ConfigureAwait(false);
			if (uid is null)
				throw new BackendException("The IMAP server did not report a UID for the appended draft.");
			return (uid.Value.Id.ToString(), RevisionOf(MessageFlags.None));
		}, ct);
	}

	public Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(itemKey);

			// EAS 16.x draft edit: content-bearing changes in the Drafts folder rewrite the
			// message (append merged draft, expunge the old one). The old UID vanishes and
			// the new one appears — the snapshot diff turns that into Delete+Add for the
			// client, which is the standard EAS re-identification flow.
			if (IsDraftsFolder(folder) && HasDraftContent(applicationData))
			{
				MimeMessage? original = null;
				try
				{
					original = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
				}
				catch (MessageNotFoundException)
				{
					// merge-from-nothing is fine — the payload becomes the whole draft
				}

				MimeMessage merged = DraftMessageBuilder.Build(applicationData, original, mailAddress);
				await folder.AppendAsync(merged, MessageFlags.Draft, ct).ConfigureAwait(false);
				await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
				await folder.ExpungeAsync(ct).ConfigureAwait(false);
				return RevisionOf(MessageFlags.None);
			}

			string? read = applicationData.Element(Email + "Read")?.Value;
			if (read is not null)
			{
				if (read == "1")
					await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct).ConfigureAwait(false);
				else
					await folder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, ct).ConfigureAwait(false);
			}

			XElement? flagElement = applicationData.Element(Email + "Flag");
			if (flagElement is not null)
			{
				string? status = flagElement.Element(Email + "Status")?.Value;
				if (status == "2")
					await folder.AddFlagsAsync(uid, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
				else
					await folder.RemoveFlagsAsync(uid, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
			}

			IList<IMessageSummary> summaries = await folder
				.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			return summaries.Count > 0 ? RevisionOf(summaries[0].Flags ?? MessageFlags.None) : "000";
		}, ct);
	}

	private static bool IsDraftsFolder(IMailFolder folder)
	{
		return folder.Attributes.HasFlag(FolderAttributes.Drafts) ||
		       DraftsNames.Contains(folder.FullName, StringComparer.OrdinalIgnoreCase) ||
		       DraftsNames.Contains(folder.Name, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>Draft-content elements, as opposed to a pure flag change (Read/Flag).</summary>
	private static bool HasDraftContent(XElement applicationData)
	{
		return applicationData.Element(Email + "To") is not null ||
		       applicationData.Element(Email + "Cc") is not null ||
		       applicationData.Element(Email2 + "Bcc") is not null ||
		       applicationData.Element(Email + "Subject") is not null ||
		       applicationData.Element(AirSyncBase + "Body") is not null ||
		       applicationData.Element(AirSyncBase + "Attachments") is not null;
	}

	public Task DeleteItemAsync(string folderBackendKey, string itemKey, CancellationToken ct, bool permanent = false)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(itemKey);
			// DeletesAsMoves=0 (permanent), or already in Trash, or no Trash folder → expunge;
			// otherwise the default move-to-Trash.
			IMailFolder? trash = permanent
				? null
				: await FindSpecialFolderAsync(client, SpecialFolder.Trash, TrashNames, ct).ConfigureAwait(false);

			if (trash is not null && !folder.FullName.Equals(trash.FullName, StringComparison.Ordinal))
			{
				await folder.MoveToAsync(uid, trash, ct).ConfigureAwait(false);
			}
			else
			{
				await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
				await folder.ExpungeAsync(ct).ConfigureAwait(false);
			}

			return true;
		}, ct);
	}

	public Task<string> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder source = await ImapSession.OpenFolderAsync(client, sourceFolderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			IMailFolder destination = await client.GetFolderAsync(
				ImapSession.FromBackendKey(destinationFolderBackendKey), ct).ConfigureAwait(false);
			UniqueId uid = ParseUid(itemKey);
			UniqueId? newUid = await source.MoveToAsync(uid, destination, ct).ConfigureAwait(false);
			return newUid?.Id.ToString()
			       ?? throw new BackendException("IMAP server did not report the moved message's new UID (no UIDPLUS).");
		}, ct);
	}

	public Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder parent = parentBackendKey is not null
				? await client.GetFolderAsync(ImapSession.FromBackendKey(parentBackendKey), ct).ConfigureAwait(false)
				: client.PersonalNamespaces.Count > 0
					? client.GetFolder(client.PersonalNamespaces[0])
					: client.Inbox;
			IMailFolder created = await parent.CreateAsync(displayName, true, ct).ConfigureAwait(false)
			                      ?? throw new BackendException("IMAP server did not return the created folder.");
			return ImapSession.ToBackendKey(created.FullName);
		}, ct);
	}

	public Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder =
				await client.GetFolderAsync(ImapSession.FromBackendKey(backendKey), ct).ConfigureAwait(false);
			IMailFolder parent = folder.ParentFolder
			                     ?? throw new BackendException("Cannot rename a namespace root folder.");
			await folder.RenameAsync(parent, newDisplayName, ct).ConfigureAwait(false);
			return true;
		}, ct);
	}

	public Task DeleteFolderAsync(string backendKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder =
				await client.GetFolderAsync(ImapSession.FromBackendKey(backendKey), ct).ConfigureAwait(false);
			await folder.DeleteAsync(ct).ConfigureAwait(false);
			return true;
		}, ct);
	}

	// ---------- IMailOperations ----------

	public async Task SendAsync(byte[] mime, CancellationToken ct)
	{
		using MemoryStream stream = new(mime);
		MimeMessage message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);

		if (smtpOptions.ForceFrom && mailAddress is not null)
		{
			string? displayName = message.From.Mailboxes.FirstOrDefault()?.Name;
			message.From.Clear();
			message.From.Add(new MailboxAddress(displayName, mailAddress));
			message.Sender = null;
		}

		// Verbose wire logging (category ActiveSync.Backends.Smtp) — attached only while
		// Trace is enabled; MailKit's secret detector masks the AUTH exchange.
		using SmtpClient smtp = smtpWireLogger?.IsEnabled(LogLevel.Trace) == true
			? new SmtpClient(new MailKitWireLogger(smtpWireLogger))
			: new SmtpClient();
		MailTransportSecurity.Apply(smtp, smtpOptions.AllowInvalidCertificates, smtpOptions.CaCertificatePath);
		await smtp.ConnectAsync(smtpOptions.Host, smtpOptions.Port, MailTransportSecurity.ForSmtp(smtpOptions), ct)
			.ConfigureAwait(false);
		await smtp.AuthenticateAsync(credentials.UserName, credentials.Password, ct).ConfigureAwait(false);
		await smtp.SendAsync(message, ct).ConfigureAwait(false);
		await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
		logger.LogInformation("Sent message {MessageId} for {User}", message.MessageId, credentials.UserName);
	}

	public Task SaveToSentAsync(byte[] mime, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder? sent = await FindSpecialFolderAsync(client, SpecialFolder.Sent, SentNames, ct).ConfigureAwait(false);
			if (sent is null)
				return false;
			using MemoryStream stream = new(mime);
			MimeMessage message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
			await sent.AppendAsync(message, MessageFlags.Seen, ct).ConfigureAwait(false);
			return true;
		}, ct);
	}

	public Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct)
	{
		return session.RunAsync<byte[]?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			try
			{
				MimeMessage message = await folder.GetMessageAsync(ParseUid(itemKey), ct).ConfigureAwait(false);
				using MemoryStream ms = new();
				await message.WriteToAsync(ms, ct).ConfigureAwait(false);
				return ms.ToArray();
			}
			catch (MessageNotFoundException)
			{
				return null;
			}
		}, ct);
	}

	public Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct)
	{
		string folderKey, itemKey;
		int index;
		try
		{
			(folderKey, itemKey, index) = ParseFileReference(fileReference);
		}
		catch (BackendException)
		{
			// Hand-crafted reference: same answer as an attachment that no longer exists.
			return Task.FromResult<BackendAttachment?>(null);
		}

		return session.RunAsync<BackendAttachment?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			MimeMessage message;
			try
			{
				message = await folder.GetMessageAsync(ParseUid(itemKey), ct).ConfigureAwait(false);
			}
			catch (MessageNotFoundException)
			{
				return null;
			}

			MimeEntity? attachment = message.Attachments.Skip(index).FirstOrDefault();
			if (attachment is not MimePart { Content: not null } part)
				return null;
			using MemoryStream ms = new();
			await part.Content.DecodeToAsync(ms, ct).ConfigureAwait(false);
			return new BackendAttachment(part.ContentType.MimeType, ms.ToArray());
		}, ct);
	}

	public Task SetAnsweredAsync(string folderBackendKey, string itemKey, bool forwarded, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(itemKey);
			if (forwarded)
				try
				{
					await folder.AddFlagsAsync(uid, MessageFlags.None, new HashSet<string> { "$Forwarded" }, true, ct)
						.ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogDebug(ex, "Server rejected $Forwarded keyword");
				}
			else
				await folder.AddFlagsAsync(uid, MessageFlags.Answered, true, ct).ConfigureAwait(false);

			return true;
		}, ct);
	}

	public Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
		string? folderBackendKey, string freeText, DateTime? sinceUtc, int maxResults, CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyList<(string, string)>>(async client =>
		{
			string folderKey = folderBackendKey ?? ImapSession.ToBackendKey(client.Inbox.FullName);
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			await client.NoOpAsync(ct).ConfigureAwait(false); // refresh the selected-folder view
			SearchQuery query = SearchQuery.SubjectContains(freeText)
				.Or(SearchQuery.FromContains(freeText))
				.Or(SearchQuery.ToContains(freeText))
				.Or(SearchQuery.BodyContains(freeText));
			if (sinceUtc is { } since)
				query = query.And(SearchQuery.DeliveredAfter(since.Date));
			IList<UniqueId> uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
			return uids
				.OrderByDescending(u => u.Id)
				.Take(maxResults)
				.Select(u => (folderKey, u.Id.ToString()))
				.ToList();
		}, ct);
	}

	public Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			if (folder.Count > 0)
			{
				await folder.AddFlagsAsync(
					Enumerable.Range(0, folder.Count).ToList(), MessageFlags.Deleted, true, ct).ConfigureAwait(false);
				await folder.ExpungeAsync(ct).ConfigureAwait(false);
			}

			return true;
		}, ct);
	}

	private static int ClassifyFolder(IMailFolder folder)
	{
		if (folder.Attributes.HasFlag(FolderAttributes.Inbox) ||
		    folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
			return EasFolderType.Inbox;
		if (folder.Attributes.HasFlag(FolderAttributes.Drafts) ||
		    DraftsNames.Contains(folder.FullName, StringComparer.OrdinalIgnoreCase))
			return EasFolderType.Drafts;
		if (folder.Attributes.HasFlag(FolderAttributes.Sent) ||
		    SentNames.Contains(folder.FullName, StringComparer.OrdinalIgnoreCase))
			return EasFolderType.SentItems;
		if (folder.Attributes.HasFlag(FolderAttributes.Trash) ||
		    TrashNames.Contains(folder.FullName, StringComparer.OrdinalIgnoreCase))
			return EasFolderType.DeletedItems;
		return EasFolderType.UserMail;
	}

	// A mail item's "revision" is a 3-digit string encoding the sync-relevant flags in a
	// fixed order: seen, flagged, answered (e.g. "101" = seen, not flagged, answered). The
	// diff engine treats any change to this string as an item change, so the digit order
	// must stay stable — a Ping/Sync watcher compares these against the stored snapshot.
	private static string RevisionOf(MessageFlags flags)
	{
		return
			$"{((flags & MessageFlags.Seen) != 0 ? 1 : 0)}{((flags & MessageFlags.Flagged) != 0 ? 1 : 0)}{((flags & MessageFlags.Answered) != 0 ? 1 : 0)}";
	}

	public static string MakeFileReference(string folderBackendKey, string itemKey, int attachmentIndex)
	{
		// Per-component escaping so a '|' inside the folder key/name cannot be mis-parsed.
		return DelimitedKey.Encode(folderBackendKey, itemKey, attachmentIndex.ToString());
	}

	public static (string FolderBackendKey, string ItemKey, int AttachmentIndex) ParseFileReference(string fileReference)
	{
		string[]? parts = DelimitedKey.Decode(fileReference, 3);
		if (parts is null || !int.TryParse(parts[2], out int index) || index < 0)
			throw new BackendException("Malformed file reference.");
		return (parts[0], parts[1], index);
	}

	/// <summary>
	///   Item keys are client-echoed strings; a non-numeric (or zero) key means the item
	///   cannot exist, reported the same way as a vanished item instead of crashing.
	/// </summary>
	private static UniqueId ParseUid(string itemKey)
	{
		return uint.TryParse(itemKey, out uint value) && value > 0
			? new UniqueId(value)
			: throw new BackendItemNotFoundException($"'{itemKey}' is not a valid mail item key.");
	}

	private static async Task<IMailFolder?> FindSpecialFolderAsync(
		ImapClient client, SpecialFolder special, string[] fallbackNames, CancellationToken ct)
	{
		try
		{
			IMailFolder? folder = client.GetFolder(special);
			if (folder is not null)
				return folder;
		}
		catch (NotSupportedException)
		{
			// server lacks SPECIAL-USE; fall through to name matching
		}

		IMailFolder? personal = client.PersonalNamespaces.Count > 0
			? client.GetFolder(client.PersonalNamespaces[0])
			: null;
		if (personal is null)
			return null;
		IList<IMailFolder> folders = await personal.GetSubfoldersAsync(false, ct).ConfigureAwait(false);
		return folders.FirstOrDefault(f => fallbackNames.Contains(f.FullName, StringComparer.OrdinalIgnoreCase))
		       ?? folders.FirstOrDefault(f => fallbackNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
	}
}
