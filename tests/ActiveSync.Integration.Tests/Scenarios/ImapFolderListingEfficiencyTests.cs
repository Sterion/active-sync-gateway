using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Folder-listing round-trip counts against a real IMAP backend, counted through the wire
///   logger's client "LIST" lines (the same technique <c>ConnectionCountingLogger</c> uses for
///   connects elsewhere in this suite).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class ImapFolderListingEfficiencyTests
{
	private static ImapOptions Options => new()
	{
		Host = TestBackend.ImapHost,
		Port = TestBackend.ImapPort,
		UseSsl = false,
		Security = "None"
	};

	/// <summary>
	///   <c>ListFoldersAsync.Walk</c> issued one LIST per folder (a non-recursive
	///   <c>GetSubfoldersAsync</c> call at every level), all under the session gate — the command
	///   count SCALED with the number of folders. A single namespace-wide LIST costs the same no
	///   matter how many folders exist (MailKit's own <c>GetFoldersAsync</c> still issues a small,
	///   CONSTANT number of LIST commands of its own — an INBOX probe plus a SPECIAL-USE pre-scan —
	///   so the invariant this proves is "constant", not "exactly one"). Proven by measuring the LIST
	///   command count against the built-in mailboxes, then again after adding six more folders, and
	///   asserting the two counts are equal.
	/// </summary>
	[BackendFact]
	public async Task ListFoldersAsync_ListCommandCount_DoesNotScaleWithFolderCount()
	{
		string user = TestBackend.User1;
		CancellationToken ct = CancellationToken.None;

		int baseline = await CountListCommandsAsync(user, ct);

		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			for (int i = 0; i < 6; i++)
				await personal.CreateAsync($"ListG14-{Guid.NewGuid():N}", true, ct);
			await raw.DisconnectAsync(true, ct);
		}

		int afterSixMoreFolders = await CountListCommandsAsync(user, ct);

		Assert.Equal(baseline, afterSixMoreFolders);
	}

	private static async Task<int> CountListCommandsAsync(string user, CancellationToken ct)
	{
		ListCommandCountingLogger wire = new();
		ImapSession session = new(Options, new BackendCredentials { UserName = user, Password = TestBackend.Password }, NullLogger.Instance, wire);
		try
		{
			ImapMailBackend backend = new(session, _ => null, NullLogger.Instance);
			await backend.ListFoldersAsync(ct);
			return wire.ListCommands;
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	/// <summary>
	///   <c>FindSpecialFolderAsync</c> re-enumerated the personal namespace (a LIST round trip)
	///   on every single delete/save-to-Sent, on servers WITHOUT SPECIAL-USE (<c>client.GetFolder
	///   (special)</c> returns null/throws, falling back to <c>personal.GetSubfoldersAsync</c> +
	///   name matching). Stalwart — the only live backend available where this item was worked —
	///   DOES advertise SPECIAL-USE, so <c>client.GetFolder(SpecialFolder.Trash)</c> resolves without
	///   ever reaching the expensive fallback branch this finding is about, on both the unmodified
	///   and the fixed code: the LIST-command count below is identical before and after the fix
	///   (COVERAGE, not a red-first reproduction of the symptom — the fix is applied on the strength
	///   of the code reading and the finding's own description of the no-SPECIAL-USE case). What this
	///   test DOES prove, on either code: two deletes in a row on the same session resolve Trash
	///   consistently and both messages actually land there.
	/// </summary>
	[BackendFact]
	public async Task DeleteItemAsync_TrashLookup_IsMemoizedPerSession_NotReEnumeratedEveryDelete()
	{
		string user = TestBackend.User1;
		string folderName = $"ItemG15-{Guid.NewGuid():N}";
		CancellationToken ct = CancellationToken.None;

		uint uidValidity, uid1, uid2;
		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			IMailFolder folder = await personal.CreateAsync(folderName, true, ct)
			                      ?? throw new InvalidOperationException("IMAP server did not return the created folder.");
			await folder.OpenAsync(FolderAccess.ReadWrite, ct);
			uidValidity = folder.UidValidity;

			MimeMessage Message(string subject) => new()
			{
				Subject = subject,
				Body = new TextPart("plain") { Text = subject }
			};
			MimeMessage m1 = Message("G15-1");
			m1.From.Add(MailboxAddress.Parse(user));
			m1.To.Add(MailboxAddress.Parse(user));
			MimeMessage m2 = Message("G15-2");
			m2.From.Add(MailboxAddress.Parse(user));
			m2.To.Add(MailboxAddress.Parse(user));

			uid1 = (await folder.AppendAsync(m1, MessageFlags.None, ct))!.Value.Id;
			uid2 = (await folder.AppendAsync(m2, MessageFlags.None, ct))!.Value.Id;
			await raw.DisconnectAsync(true, ct);
		}

		ListCommandCountingLogger wire = new();
		ImapSession session = new(Options, new BackendCredentials { UserName = user, Password = TestBackend.Password }, NullLogger.Instance, wire);
		try
		{
			ImapMailBackend backend = new(session, _ => null, NullLogger.Instance);
			string folderKey = ImapSession.ToBackendKey(folderName);

			await backend.DeleteItemAsync(new FolderKey(folderKey), new ItemKey($"{uidValidity}:{uid1}"), false, ct);
			int afterFirstDelete = wire.ListCommands;

			await backend.DeleteItemAsync(new FolderKey(folderKey), new ItemKey($"{uidValidity}:{uid2}"), false, ct);
			int afterSecondDelete = wire.ListCommands;

			// Coverage on this backend (see the doc comment above): both counts are the same
			// because Stalwart's SPECIAL-USE support means neither delete ever reaches the
			// expensive fallback branch, not because the memoization added here proved anything.
			Assert.Equal(afterFirstDelete, afterSecondDelete);
		}
		finally
		{
			await session.DisposeAsync();
		}

		// Functional regression guard: both deletes must actually have routed to Trash, not just
		// left the count assertion above satisfied by coincidence.
		using ImapClient verify = new();
		await verify.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
		await verify.AuthenticateAsync(user, TestBackend.Password, ct);
		IMailFolder trash = verify.GetFolder(SpecialFolder.Trash)
		                     ?? throw new InvalidOperationException("IMAP server has no Trash special-use folder.");
		await trash.OpenAsync(FolderAccess.ReadOnly, ct);
		IList<UniqueId> trashed = await trash.SearchAsync(
			MailKit.Search.SearchQuery.HeaderContains("Subject", "G15-"), ct);
		Assert.True(trashed.Count >= 2, $"expected both G15 messages in Trash, found {trashed.Count}");
		await verify.DisconnectAsync(true, ct);
	}

	/// <summary>
	///   Counts IMAP LIST commands by watching for the wire logger's client ("C:") lines containing
	///   "LIST". Trace must be enabled (<c>IsEnabled</c> returns true) or
	///   <c>ImapConnectionFactory</c> does not attach the wire logger at all.
	/// </summary>
	private sealed class ListCommandCountingLogger : ILogger
	{
		private int _listCommands;

		public int ListCommands => Volatile.Read(ref _listCommands);

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(
			LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			string line = formatter(state, exception);
			if (line.Contains("] C:", StringComparison.Ordinal) &&
			    line.Contains("LIST", StringComparison.Ordinal))
				Interlocked.Increment(ref _listCommands);
		}
	}
}
