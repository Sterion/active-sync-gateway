using System.Xml.Linq;
using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Round 3, item 24 — IMAP correctness (<c>G3</c>, <c>G6</c>, <c>G7</c>, <c>G9</c>, <c>G12</c>,
///   <c>G13</c>, <c>G16</c>, <c>G22</c>). White-box: each test constructs
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
		ImapSession session = new(Options, new BackendCredentials(user, TestBackend.Password), NullLogger.Instance);
		ImapMailBackend backend = new(session, user, _ => null, NullLogger.Instance);
		return (session, backend);
	}

	// G3 -----------------------------------------------------------------------------------

	/// <summary>
	///   G3: an unqualified (no "&lt;uidvalidity&gt;:" prefix) mail item key silently gets the
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
			// hand-crafted shape G3 describes. Correct behavior: refused as not-found, never
			// silently resolved against the folder's current UidValidity.
			await Assert.ThrowsAsync<BackendItemNotFoundException>(
				() => backend.DeleteItemAsync(folderKey, uid.ToString(), true, ct));
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

	// G12 ----------------------------------------------------------------------------------

	/// <summary>
	///   G12: <c>ClassifyFolder</c> (FolderSync's Type element) matches only a special folder's
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
			BackendFolder nested = Assert.Single(folders, f => f.BackendKey == ImapSession.ToBackendKey(draftsFullName));

			// The write path (draft create) already treats this folder as Drafts (IsDraftsFolder
			// matches on the leaf Name) — FolderSync's classification must agree, or the phone
			// never shows a Drafts folder it can actually compose into.
			(string ItemKey, string Revision) created = await backend.CreateItemAsync(
				nested.BackendKey,
				new XElement("ApplicationData",
					new XElement(EasNamespaces.Email + "Subject", "G12 draft"),
					new XElement(EasNamespaces.AirSyncBase + "Body",
						new XElement(EasNamespaces.AirSyncBase + "Type", "1"),
						new XElement(EasNamespaces.AirSyncBase + "Data", "g12-body"))),
				ct);
			Assert.NotNull(created.ItemKey);

			Assert.Equal(EasFolderType.Drafts, nested.EasType);
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	// G16 ----------------------------------------------------------------------------------

	/// <summary>
	///   G16: a content-bearing Change to a mail item OUTSIDE the Drafts folder falls through the
	///   Drafts-only rewrite branch straight into the Read/Flag/Categories handling, which ignores
	///   the content elements and returns a fresh revision — the client is told Status 1 (applied)
	///   while the edit was silently discarded. <c>CreateItemAsync</c> already refuses the
	///   analogous case explicitly; this proves <c>UpdateItemAsync</c> does not.
	/// </summary>
	[BackendFact]
	public async Task UpdateItemAsync_ContentChangeOutsideDrafts_IsRejected_NotSilentlyDiscarded()
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
			XElement contentChange = new("ApplicationData",
				new XElement(EasNamespaces.Email + "Subject", "g16-hijacked-subject"));

			// This is NOT the Drafts folder, so a content-bearing Change (a Subject edit) must be
			// refused — mirroring CreateItemAsync's explicit refusal of the analogous case — not
			// silently accepted with the content dropped.
			await Assert.ThrowsAsync<BackendException>(
				() => backend.UpdateItemAsync(folderKey, itemKey, contentChange, ct));
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

	// G13 ----------------------------------------------------------------------------------

	/// <summary>
	///   G13: <c>SearchAsync</c> applies the raw <c>since.Date</c> as the SINCE floor, while
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
			string folderKey = ImapSession.ToBackendKey(folderName);
			IReadOnlyList<(string FolderBackendKey, string ItemKey)> results =
				await backend.SearchAsync(folderKey, subject, since, 10, ct);

			// GetItemRevisionsAsync (the sync-filter path) already applies SearchFloor and would
			// keep this message; Search must not be a narrower view of the same folder.
			Assert.Contains(results, r => r.FolderBackendKey == folderKey);
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	// G22 ----------------------------------------------------------------------------------

	/// <summary>
	///   G22: <c>SnapshotStatusAsync</c> (the STATUS-poll fallback that backs a Ping's push
	///   detection) goes through the SAME per-session gate as <c>GetItemRevisionsAsync</c>'s
	///   whole-mailbox FETCH, so a long-held FETCH on one device blocks another device's Ping
	///   STATUS poll for the same user behind it. Proven deterministically: hold the session's gate
	///   with an artificial delay (standing in for a slow FETCH — the mechanism, not the timing, is
	///   what's under test) and measure whether a concurrent <c>WaitForChangesAsync</c> call is
	///   blocked behind it.
	/// </summary>
	[BackendFact]
	public async Task WaitForChangesAsync_IsNotBlockedBehindAConcurrentLongHeldSessionGate()
	{
		string user = TestBackend.User1;
		CancellationToken ct = CancellationToken.None;
		(ImapSession session, ImapMailBackend backend) = CreateBackend(user);
		try
		{
			string folderKey = ImapSession.ToBackendKey("INBOX");

			// Stands in for GetItemRevisionsAsync's long-held whole-mailbox FETCH: any operation
			// that occupies the session's gate for a while.
			Task slowHold = session.RunAsync(
				async _ => { await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None); return true; }, ct);
			await Task.Delay(TimeSpan.FromMilliseconds(200), ct); // let it win the gate first

			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
			await backend.WaitForChangesAsync([folderKey], TimeSpan.FromMilliseconds(200), ct);
			stopwatch.Stop();

			await slowHold;

			// A push-detection poll for one device must not queue behind another device's
			// long-running backend call on the same session gate.
			Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5),
				$"WaitForChangesAsync took {stopwatch.Elapsed} while a 2s operation held the session gate " +
				"-- it should use its own connection, not queue behind it.");
		}
		finally
		{
			await session.DisposeAsync();
		}
	}
}
