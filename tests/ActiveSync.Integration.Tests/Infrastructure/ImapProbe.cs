using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace ActiveSync.Integration.Tests.Infrastructure;

/// <summary>
///   Direct-to-backend IMAP probes/mutations used by scenario tests to set up or verify state
///   out of band (i.e. not through the gateway). Each call is a fresh, short-lived connection.
/// </summary>
internal static class ImapProbe
{
	private static async Task<ImapClient> ConnectAsync(string user)
	{
		ImapClient imap = new();
		await imap.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None);
		await imap.AuthenticateAsync(user, TestBackend.Password);
		return imap;
	}

	public static async Task<bool> MessageHasFlagAsync(
		string user, string folder, string subject, MessageFlags flag)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder = await imap.GetFolderAsync(folder);
		await mailFolder.OpenAsync(FolderAccess.ReadOnly);
		IList<UniqueId> uids = await mailFolder.SearchAsync(SearchQuery.SubjectContains(subject));
		if (uids.Count == 0)
			return false;
		IList<IMessageSummary> summaries = await mailFolder.FetchAsync(uids, MessageSummaryItems.Flags);
		await imap.DisconnectAsync(true);
		return summaries.Any(s => (s.Flags ?? MessageFlags.None).HasFlag(flag));
	}

	public static async Task<bool> MessageExistsAsync(string user, string folder, string subject)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder;
		try
		{
			mailFolder = await imap.GetFolderAsync(folder);
		}
		catch (FolderNotFoundException)
		{
			return false; // a message cannot exist in a folder the server never created
		}

		await mailFolder.OpenAsync(FolderAccess.ReadOnly);
		IList<UniqueId> uids = await mailFolder.SearchAsync(SearchQuery.SubjectContains(subject));
		await imap.DisconnectAsync(true);
		return uids.Count > 0;
	}

	public static async Task<IReadOnlyList<string>> MessageKeywordsAsync(
		string user, string folder, string subject)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder = await imap.GetFolderAsync(folder);
		await mailFolder.OpenAsync(FolderAccess.ReadOnly);
		IList<UniqueId> uids = await mailFolder.SearchAsync(SearchQuery.SubjectContains(subject));
		if (uids.Count == 0)
			return [];
		IList<IMessageSummary> summaries = await mailFolder.FetchAsync(uids, MessageSummaryItems.Flags);
		await imap.DisconnectAsync(true);
		return summaries.SelectMany(s => s.Keywords ?? (IReadOnlySet<string>)new HashSet<string>())
			.Distinct().ToList();
	}

	public static async Task AddKeywordAsync(string user, string subject, string keyword)
	{
		using ImapClient imap = await ConnectAsync(user);
		await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
		IList<UniqueId> uids = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
		Assert.NotEmpty(uids);
		await imap.Inbox.AddFlagsAsync(uids, MessageFlags.None, new HashSet<string> { keyword }, true);
		await imap.DisconnectAsync(true);
	}

	/// <summary>
	///   Marks a message <c>\Deleted</c> without expunging — what every other IMAP client does
	///   between "user pressed delete" and "user emptied the folder". Nothing the gateway does to
	///   an unrelated message may remove it.
	/// </summary>
	public static async Task SetDeletedAsync(string user, string folder, string subject)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder = await imap.GetFolderAsync(folder);
		await mailFolder.OpenAsync(FolderAccess.ReadWrite);
		IList<UniqueId> uids = await mailFolder.SearchAsync(SearchQuery.SubjectContains(subject));
		Assert.NotEmpty(uids);
		await mailFolder.AddFlagsAsync(uids, MessageFlags.Deleted, true);
		await imap.DisconnectAsync(true);
	}

	/// <summary>Creates a top-level folder and returns its UIDVALIDITY.</summary>
	public static async Task<uint> CreateFolderAsync(string user, string name)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder personal = imap.GetFolder(imap.PersonalNamespaces[0]);
		IMailFolder? created = await personal.CreateAsync(name, true);
		Assert.NotNull(created);
		await created.OpenAsync(FolderAccess.ReadOnly);
		uint validity = created.UidValidity;
		await imap.DisconnectAsync(true);
		return validity;
	}

	public static async Task DeleteFolderAsync(string user, string name)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder folder = await imap.GetFolderAsync(name);
		await folder.DeleteAsync();
		await imap.DisconnectAsync(true);
	}

	/// <summary>Appends a trivial message to a folder and returns the UID the server assigned.</summary>
	public static async Task<uint> AppendAsync(string user, string folder, string subject)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder = await imap.GetFolderAsync(folder);
		await mailFolder.OpenAsync(FolderAccess.ReadWrite);
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse(user));
		message.To.Add(MailboxAddress.Parse(user));
		message.Subject = subject;
		message.Body = new TextPart("plain") { Text = "body" };
		UniqueId? uid = await mailFolder.AppendAsync(message);
		await imap.DisconnectAsync(true);
		Assert.NotNull(uid);
		return uid.Value.Id;
	}

	public static async Task<int> CountMessagesAsync(string user, string folder, string subject)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder = await imap.GetFolderAsync(folder);
		await mailFolder.OpenAsync(FolderAccess.ReadOnly);
		IList<UniqueId> uids = await mailFolder.SearchAsync(SearchQuery.SubjectContains(subject));
		await imap.DisconnectAsync(true);
		return uids.Count;
	}

	public static async Task SetSeenAsync(string user, string subject, bool seen)
	{
		using ImapClient imap = await ConnectAsync(user);
		await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
		IList<UniqueId> uids = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
		Assert.NotEmpty(uids);
		if (seen)
			await imap.Inbox.AddFlagsAsync(uids, MessageFlags.Seen, true);
		else
			await imap.Inbox.RemoveFlagsAsync(uids, MessageFlags.Seen, true);
		await imap.DisconnectAsync(true);
	}
}
