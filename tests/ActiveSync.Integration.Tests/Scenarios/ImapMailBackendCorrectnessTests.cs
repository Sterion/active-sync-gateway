using System.Xml.Linq;
using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   IMAP correctness coverage. White-box: each test constructs
///   <see cref="ImapMailBackend" />/<see cref="ImapSession" /> directly against the real IMAP
///   backend (bypassing the EAS wire protocol) so it can drive the exact conditions each finding
///   describes, mirroring the direct-construction pattern already used for DAV findings (e.g.
///   <see cref="DavCreatePutReplayTests" />).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class ImapMailBackendCorrectnessTests
{
	private static ImapOptions Options => new()
	{
		Host = TestBackend.ImapHost,
		Port = TestBackend.ImapPort,
		UseSsl = false,
		Security = "None"
	};

	private static (ImapSession Session, ImapMailBackend Backend) CreateBackend(string user)
	{
		ImapSession session = new(Options, new BackendCredentials { UserName = user, Password = TestBackend.Password }, NullLogger.Instance);
		ImapMailBackend backend = new(session, _ => null, NullLogger.Instance);
		return (session, backend);
	}

	// Unqualified item key rejection --------------------------------------------------------

	/// <summary>
	///   An unqualified (no "&lt;uidvalidity&gt;:" prefix) mail item key silently gets the
	///   folder's CURRENT UidValidity stamped onto it instead of being rejected, so a stale or
	///   hand-crafted key can address whatever message now holds that UID number. Proven against a
	///   real mailbox: append a message, then delete it by UID alone (no validity prefix) — the
	///   unqualified form must be refused, not resolved.
	/// </summary>
	[BackendFact]
	public async Task DeleteItemAsync_UnqualifiedItemKey_IsRejected_NotSilentlyResolved()
	{
		string user = TestBackend.User1;
		string folderName = $"ItemG3-{Guid.NewGuid():N}";
		CancellationToken ct = CancellationToken.None;

		uint uid;
		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			IMailFolder folder = await personal.CreateAsync(folderName, true, ct)
			                      ?? throw new InvalidOperationException("IMAP server did not return the created folder.");
			await folder.OpenAsync(FolderAccess.ReadWrite, ct);
			MimeMessage message = new();
			message.From.Add(MailboxAddress.Parse(user));
			message.To.Add(MailboxAddress.Parse(user));
			message.Subject = "G3";
			message.Body = new TextPart("plain") { Text = "g3-body" };
			UniqueId? appended = await folder.AppendAsync(message, MessageFlags.None, ct);
			Assert.NotNull(appended);
			uid = appended.Value.Id;
			await raw.DisconnectAsync(true, ct);
		}

		(ImapSession session, ImapMailBackend backend) = CreateBackend(user);
		try
		{
			string folderKey = ImapSession.ToBackendKey(folderName);
			// The client-echoed key carries NO "<uidvalidity>:" prefix — exactly the legacy/
			// hand-crafted shape described above. Correct behavior: refused as not-found, never
			// silently resolved against the folder's current UidValidity.
			await Assert.ThrowsAsync<BackendItemNotFoundException>(
				() => backend.DeleteItemAsync(new FolderKey(folderKey), new ItemKey(uid.ToString()), true, ct));
		}
		finally
		{
			await session.DisposeAsync();
		}

		// The message must still exist — an unqualified key must never resolve to a real delete.
		using ImapClient verify = new();
		await verify.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
		await verify.AuthenticateAsync(user, TestBackend.Password, ct);
		IMailFolder verifyFolder = await verify.GetFolderAsync(folderName, ct);
		await verifyFolder.OpenAsync(FolderAccess.ReadOnly, ct);
		Assert.Equal(1, verifyFolder.Count);
		await verify.DisconnectAsync(true, ct);
	}

	// Nested Drafts folder classification ---------------------------------------------------

	/// <summary>
	///   <c>ClassifyFolder</c> (FolderSync's Type element) matches only a special folder's
	///   FullName, while <c>IsDraftsFolder</c> (the Sync write-path gate) also matches the leaf
	///   Name — so a server without SPECIAL-USE that nests Drafts under a non-INBOX parent (no
	///   FullName match) is reported to the phone as an ordinary UserMail folder while the backend
	///   still accepts draft creates/edits there. Proven against a real mailbox: create
	///   "&lt;parent&gt;/Drafts" and check that ListFoldersAsync's reported Type agrees with
	///   whether CreateItemAsync actually treats it as Drafts.
	/// </summary>
	[BackendFact]
	public async Task ListFoldersAsync_NestedDraftsFolder_IsClassifiedConsistentlyWithTheWritePath()
	{
		string user = TestBackend.User1;
		string parentName = $"ItemG12-{Guid.NewGuid():N}";
		CancellationToken ct = CancellationToken.None;
		string draftsFullName;

		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			IMailFolder parent = await personal.CreateAsync(parentName, false, ct)
			                      ?? throw new InvalidOperationException("IMAP server did not return the created parent.");
			// A leaf named "Drafts" nested under a non-INBOX parent, with no SPECIAL-USE
			// attribute the server would only assign to a canonical top-level Drafts.
			IMailFolder drafts = await parent.CreateAsync("Drafts", true, ct)
			                     ?? throw new InvalidOperationException("IMAP server did not return the created folder.");
			draftsFullName = drafts.FullName;
			await raw.DisconnectAsync(true, ct);
		}

		(ImapSession session, ImapMailBackend backend) = CreateBackend(user);
		try
		{
			IReadOnlyList<BackendFolder> folders = await backend.ListFoldersAsync(ct);
			BackendFolder nested = Assert.Single(folders, f => f.Key.Value == ImapSession.ToBackendKey(draftsFullName));

			// The write path (draft create) already treats this folder as Drafts (IsDraftsFolder
			// matches on the leaf Name) — FolderSync's classification must agree, or the phone
			// never shows a Drafts folder it can actually compose into. The host builds the draft
			// MIME now, so the store is handed a finished message.
			(ItemKey Key, ItemRevision Revision) created = await backend.CreateDraftAsync(
				nested.Key,
				new MailItem
				{
					Rfc822 = System.Text.Encoding.ASCII.GetBytes(
						$"From: {user}\r\nTo: {user}\r\nSubject: G12 draft\r\n\r\ng12-body\r\n"),
					Flags = new MailFlags { Draft = true }
				},
				ct);
			Assert.False(string.IsNullOrEmpty(created.Key.Value));

			Assert.Equal(FolderType.Drafts, nested.Type);
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	// Content change outside Drafts ----------------------------------------------------------

	/// <summary>
	///   A content-bearing Change to a mail item OUTSIDE the Drafts folder must be refused, not
	///   silently discarded while the client is told Status 1 (applied). Under the typed seam the
	///   host routes a content-bearing Change to <c>ReplaceDraftAsync</c>, and the store re-refuses
	///   any folder but Drafts — the same explicit refusal <c>CreateDraftAsync</c> carries.
	/// </summary>
	[BackendFact]
	public async Task ReplaceDraftAsync_OutsideDrafts_IsRejected_NotSilentlyDiscarded()
	{
		string user = TestBackend.User1;
		string folderName = $"ItemG16-{Guid.NewGuid():N}";
		CancellationToken ct = CancellationToken.None;
		string itemKey;
		string originalSubject = $"g16-original-{Guid.NewGuid():N}";

		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			IMailFolder folder = await personal.CreateAsync(folderName, true, ct)
			                      ?? throw new InvalidOperationException("IMAP server did not return the created folder.");
			await folder.OpenAsync(FolderAccess.ReadWrite, ct);
			MimeMessage message = new();
			message.From.Add(MailboxAddress.Parse(user));
			message.To.Add(MailboxAddress.Parse(user));
			message.Subject = originalSubject;
			message.Body = new TextPart("plain") { Text = "g16-body" };
			UniqueId? appended = await folder.AppendAsync(message, MessageFlags.None, ct);
			Assert.NotNull(appended);
			itemKey = $"{folder.UidValidity}:{appended.Value.Id}";
			await raw.DisconnectAsync(true, ct);
		}

		(ImapSession session, ImapMailBackend backend) = CreateBackend(user);
		try
		{
			string folderKey = ImapSession.ToBackendKey(folderName);
			MailItem replacement = new()
			{
				Rfc822 = System.Text.Encoding.ASCII.GetBytes(
					$"From: {user}\r\nTo: {user}\r\nSubject: g16-hijacked-subject\r\n\r\ng16-body\r\n"),
				Flags = new MailFlags { Draft = true }
			};

			// This is NOT the Drafts folder, so a content rewrite must be refused — mirroring
			// CreateDraftAsync's explicit refusal of the analogous case — not silently accepted
			// with the content dropped.
			await Assert.ThrowsAsync<BackendException>(
				() => backend.ReplaceDraftAsync(new FolderKey(folderKey), new ItemKey(itemKey), replacement, ct));
		}
		finally
		{
			await session.DisposeAsync();
		}

		// The stored message must be untouched.
		using ImapClient verify = new();
		await verify.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
		await verify.AuthenticateAsync(user, TestBackend.Password, ct);
		IMailFolder verifyFolder = await verify.GetFolderAsync(folderName, ct);
		await verifyFolder.OpenAsync(FolderAccess.ReadOnly, ct);
		IList<UniqueId> uids = await verifyFolder.SearchAsync(MailKit.Search.SearchQuery.All, ct);
		Assert.Single(uids);
		MimeMessage stored = await verifyFolder.GetMessageAsync(uids[0], ct);
		Assert.Equal(originalSubject, stored.Subject);
		await verify.DisconnectAsync(true, ct);
	}

	// SearchFloor date-window widening -------------------------------------------------------

	/// <summary>
	///   <c>SearchAsync</c> applies the raw <c>since.Date</c> as the SINCE floor, while
	///   <c>GetItemRevisionsAsync</c> applies <c>SearchFloor(since)</c> (backed off one extra day —
	///   RFC 3501 SINCE compares the server's own INTERNALDATE calendar day and disregards
	///   timezone). <c>SearchFloor</c>'s widening makes the sync-filter query a strict SUPERSET of
	///   the raw one, so a message dated in that one-day gap is a result the sync filter would keep
	///   but Search misses. Proven deterministically: append a message with an explicit INTERNALDATE
	///   exactly one day before <c>since</c> and confirm Search finds it once it uses the same floor.
	/// </summary>
	[BackendFact]
	public async Task SearchAsync_UsesTheSameWidenedFloor_AsGetItemRevisionsAsync()
	{
		string user = TestBackend.User1;
		string folderName = $"ItemG13-{Guid.NewGuid():N}";
		CancellationToken ct = CancellationToken.None;
		string subject = $"g13-{Guid.NewGuid():N}";

		// A fixed, deterministic boundary — not "now" — so the test never straddles a real
		// midnight. `since` is midnight on day D; the message's INTERNALDATE is noon on day D-1,
		// i.e. inside SearchFloor(since) = (D-1).Date but outside the raw since.Date.
		// since.AddDays(-1).Date is exactly ImapMailBackend.SearchFloor(since)'s formula
		// (unit-tested directly in ImapSearchFloorTests) — inlined here rather than calling the
		// internal method, since this project has no InternalsVisibleTo grant into the backend.
		DateTime since = new(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
		DateTimeOffset internalDate = new(2024, 6, 14, 12, 0, 0, TimeSpan.Zero);
		Assert.Equal(since.AddDays(-1).Date, internalDate.UtcDateTime.Date);

		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			IMailFolder folder = await personal.CreateAsync(folderName, true, ct)
			                      ?? throw new InvalidOperationException("IMAP server did not return the created folder.");
			await folder.OpenAsync(FolderAccess.ReadWrite, ct);
			MimeMessage message = new();
			message.From.Add(MailboxAddress.Parse(user));
			message.To.Add(MailboxAddress.Parse(user));
			message.Subject = subject;
			message.Body = new TextPart("plain") { Text = "g13-body" };
			await folder.AppendAsync(message, MessageFlags.None, internalDate, ct);
			await raw.DisconnectAsync(true, ct);
		}

		(ImapSession session, ImapMailBackend backend) = CreateBackend(user);
		try
		{
			FolderKey folderKey = new(ImapSession.ToBackendKey(folderName));
			IReadOnlyList<SearchHit> results = await backend.SearchAsync(folderKey, subject, since, 10, ct);

			// GetItemRevisionsAsync (the sync-filter path) already applies SearchFloor and would
			// keep this message; Search must not be a narrower view of the same folder.
			Assert.Contains(results, r => r.Folder == folderKey);
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	// Persistent poll connection --------------------------------------------------------------

	/// <summary>
	///   A prior fix for this was rolled back for opening a connection per poll instead of reusing one.
	///   <c>SnapshotStatusAsync</c>, the STATUS poll that backs every Ping/Sync long-poll, ran
	///   through the SAME per-session gate as <c>GetItemRevisionsAsync</c>'s whole-mailbox FETCH,
	///   so one device's Sync round stalled every other device's push detection behind it.
	///   The remedy is a PERSISTENT provider-owned poll connection per user (as
	///   <see cref="ImapIdleWatcher" /> already has), not a fresh connection per poll — so this
	///   test pins BOTH halves at once:
	///   <list type="bullet">
	///     <item>hold the session gate for the whole run and assert the polls are not blocked</item>
	///     <item>count the IMAP connections actually opened and assert it stays at exactly two —
	///     the session's own plus ONE poll connection, reused across every snapshot</item>
	///   </list>
	///   Connections are counted through MailKit's protocol logger ("Connected to …", emitted once
	///   per connect by <c>MailKitWireLogger.LogConnect</c>), which needs no test seam in
	///   production code and counts the session and the poller alike.
	/// </summary>
	[BackendFact]
	public async Task WaitForChangesAsync_PollsOverOneOwnConnection_NotTheSessionGate()
	{
		string user = TestBackend.User1;
		CancellationToken ct = CancellationToken.None;
		ConnectionCountingLogger wire = new();
		BackendCredentials credentials = new() { UserName = user, Password = TestBackend.Password };
		ImapSession session = new(Options, credentials, NullLogger.Instance, wire);
		ImapStatusPoller poller = new(Options, credentials, NullLogger.Instance, wire);
		ImapMailBackend backend = new(session, _ => null, NullLogger.Instance, () => poller);
		try
		{
			string folderKey = ImapSession.ToBackendKey("INBOX");

			// Stands in for GetItemRevisionsAsync's long-held whole-mailbox FETCH: any operation
			// that occupies the session's gate for a while. The TCS fires from INSIDE the gate, so
			// the polls below are guaranteed to arrive while it is held.
			TaskCompletionSource gateHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Task slowHold = session.RunAsync(async _ =>
			{
				gateHeld.SetResult();
				await Task.Delay(TimeSpan.FromSeconds(8), CancellationToken.None);
				return true;
			}, ct);
			await gateHeld.Task;
			Assert.Equal(1, wire.Connections); // just the session's own

			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
			for (int i = 0; i < 4; i++)
				await backend.WaitForChangesAsync([new FolderKey(folderKey)], TimeSpan.FromMilliseconds(300), ct);
			stopwatch.Stop();

			await slowHold;

			// Push detection for one device must not queue behind another device's long-running
			// backend call on the same session gate.
			Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6),
				$"Four poll rounds took {stopwatch.Elapsed} while an 8s operation held the session gate " +
				"-- they should run on their own connection, not queue behind it.");

			// ...and that own connection must be PERSISTENT. Four WaitForChangesAsync rounds issue
			// eight STATUS snapshots; a poll that reconnects per call would count nine here, which
			// is exactly the regression that got the first fix rolled back.
			Assert.Equal(2, wire.Connections);
		}
		finally
		{
			await poller.DisposeAsync();
			await session.DisposeAsync();
		}
	}

	/// <summary>
	///   Counts IMAP connections by watching for MailKit's per-connection
	///   <c>IProtocolLogger.LogConnect</c> line. Trace must be enabled or
	///   <c>ImapConnectionFactory</c> does not attach the wire logger at all.
	/// </summary>
	private sealed class ConnectionCountingLogger : ILogger
	{
		private int _connections;

		public int Connections => Volatile.Read(ref _connections);

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
			if (formatter(state, exception).Contains("Connected to", StringComparison.Ordinal))
				Interlocked.Increment(ref _connections);
		}
	}
}
