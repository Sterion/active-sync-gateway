using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   F10: a 16.x draft submit (email2:Send) that succeeds at the SMTP seam but then fails filing to
///   Sent must still be reported as success, and must record the replay marker — otherwise the per-
///   command catch turns an already-sent mail into Status 6, the client resends, and the recipient
///   gets it twice. Drives <see cref="SyncHandler.ApplyClientCommandAsync" /> directly (it is the seam
///   the Sync collection loop wraps in the Status-6 catch).
/// </summary>
public sealed class DraftSubmitIdempotencyTests : IDisposable
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;
	private static readonly XNamespace E2 = EasNamespaces.Email2;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	[Fact]
	public async Task DraftSubmit_WhenFilingToSentFails_StillSucceedsAndRecordsReplayMarker()
	{
		List<UserFolder> folders = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:Drafts", "Drafts", null, EasFolderType.Drafts, EasClass.Email));
		UserFolder drafts = folders.Single(f => f.BackendKey == "imap:Drafts");

		// The submit succeeds; filing to Sent — a best-effort follow-up — fails.
		_harness.Session.Mail.SaveToSentShouldThrow = true;

		SyncHandler handler = NewSyncHandler();
		EasContext context = await _harness.NewContextAsync();
		ClientCommandLedger ledger = ClientCommandLedger.Empty();

		XElement command = new(AS + "Add",
			new XElement(AS + "ClientId", "c1"),
			new XElement(AS + "ApplicationData",
				new XElement(Email + "To", "dest@example.com"),
				new XElement(Email + "Subject", "Hi"),
				new XElement(ASB + "Body",
					new XElement(ASB + "Type", "1"),
					new XElement(ASB + "Data", "hello there")),
				new XElement(E2 + "Send")));

		XElement? result = await handler.ApplyClientCommandAsync(
			context, drafts, _harness.Session.Store, command,
			new Dictionary<string, string>(StringComparer.Ordinal),
			new BodyPreference(1, null, false), deletesAsMoves: true, ledger, syncKeyForClaim: 1,
			CancellationToken.None);

		// A successful submission is reported as success, not the Status 6 the catch would emit.
		Assert.Equal("1", result?.Element(AS + "Status")?.Value);
		// It went out exactly once…
		Assert.Single(_harness.Session.Submit.Sent);
		// …the best-effort file to Sent was reached (and swallowed)…
		Assert.True(_harness.Session.Mail.SaveToSentAttempted);
		// …and the replay marker is recorded, so a client resend replays instead of re-sending.
		Assert.True(ledger.AppliedAdds.ContainsKey("c1"));
	}

	// F2: a crash between the SMTP send and this collection's own CommitCollectionStateAsync
	// leaves the SyncKey unadvanced, so a client resend validates as Current — a FRESH, EMPTY
	// ledger, not a Replay of the one that recorded the first attempt (Replay only ever happens
	// once the round has already committed and the key has already advanced once). Simulate the
	// crash by driving ApplyClientCommandAsync twice for the identical Add/ClientId, each with its
	// own independent empty ledger — exactly what two Sync requests under the same never-advanced
	// SyncKey would each build.
	[Fact]
	public async Task DraftSubmit_ResentAfterACrashBeforeCommit_DoesNotSubmitTwice()
	{
		List<UserFolder> folders = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:Drafts", "Drafts", null, EasFolderType.Drafts, EasClass.Email));
		UserFolder drafts = folders.Single(f => f.BackendKey == "imap:Drafts");

		SyncHandler handler = NewSyncHandler();
		EasContext context = await _harness.NewContextAsync();

		XElement CommandFor(string clientId) => new(AS + "Add",
			new XElement(AS + "ClientId", clientId),
			new XElement(AS + "ApplicationData",
				new XElement(Email + "To", "dest@example.com"),
				new XElement(Email + "Subject", "Hi"),
				new XElement(ASB + "Body",
					new XElement(ASB + "Type", "1"),
					new XElement(ASB + "Data", "hello there")),
				new XElement(E2 + "Send")));

		// Attempt 1: the SMTP send goes out, then the process crashes before this collection's
		// round commits — so nothing durable ever records that ClientId "c1" was already sent. Both
		// attempts present the SAME (never-advanced) SyncKey, exactly as two real Sync requests
		// under a round that never committed would.
		await handler.ApplyClientCommandAsync(
			context, drafts, _harness.Session.Store, CommandFor("c1"),
			new Dictionary<string, string>(StringComparer.Ordinal),
			new BodyPreference(1, null, false), deletesAsMoves: true, ClientCommandLedger.Empty(),
			syncKeyForClaim: 1, CancellationToken.None);

		// Attempt 2: the client, having never seen a response, resends the identical Add under the
		// same (unadvanced) SyncKey — validated as Current, so the real collection loop would build
		// a FRESH, empty ledger exactly like the first attempt's.
		await handler.ApplyClientCommandAsync(
			context, drafts, _harness.Session.Store, CommandFor("c1"),
			new Dictionary<string, string>(StringComparer.Ordinal),
			new BodyPreference(1, null, false), deletesAsMoves: true, ClientCommandLedger.Empty(),
			syncKeyForClaim: 1, CancellationToken.None);

		// The draft must have gone out over SMTP exactly once — not once per attempt.
		Assert.Single(_harness.Session.Submit.Sent);
	}

	// F2-followup: the crash-duplicate guard must not become a data-loss guard. A crash is not the
	// only way an attempt can be claimed and never complete -- an ORDINARY transient send failure
	// (SMTP backend down, network blip) does the exact same thing: the claim lands durably, then
	// SubmitDraftAsync throws. If the claim primitive treats "claimed" as "already sent" (the
	// original F2 shape), the client's resend under the same unadvanced SyncKey finds the claim,
	// skips the send entirely, and reports Status 1 -- the mail is silently lost and the client is
	// told it went out. Prove the resend actually retries the send instead.
	[Fact]
	public async Task DraftSubmit_ResentAfterATransientSendFailure_ActuallyResendsInsteadOfReportingFalseSuccess()
	{
		List<UserFolder> folders = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:Drafts", "Drafts", null, EasFolderType.Drafts, EasClass.Email));
		UserFolder drafts = folders.Single(f => f.BackendKey == "imap:Drafts");

		SyncHandler handler = NewSyncHandler();
		EasContext context = await _harness.NewContextAsync();

		XElement CommandFor(string clientId) => new(AS + "Add",
			new XElement(AS + "ClientId", clientId),
			new XElement(AS + "ApplicationData",
				new XElement(Email + "To", "dest@example.com"),
				new XElement(Email + "Subject", "Hi"),
				new XElement(ASB + "Body",
					new XElement(ASB + "Type", "1"),
					new XElement(ASB + "Data", "hello there")),
				new XElement(E2 + "Send")));

		// Attempt 1: the SMTP backend is unreachable -- a routine, transient failure, not a crash.
		// The claim is (correctly) durably recorded BEFORE the send, but the send itself never goes
		// out; the exception must surface so the client knows to retry.
		_harness.Session.Submit.FailWith = () => new BackendException("smtp backend unreachable");

		await Assert.ThrowsAsync<BackendException>(() => handler.ApplyClientCommandAsync(
			context, drafts, _harness.Session.Store, CommandFor("c1"),
			new Dictionary<string, string>(StringComparer.Ordinal),
			new BodyPreference(1, null, false), deletesAsMoves: true, ClientCommandLedger.Empty(),
			syncKeyForClaim: 1, CancellationToken.None));

		Assert.Empty(_harness.Session.Submit.Sent); // nothing went out on the failed attempt

		// Attempt 2: the backend recovers; the client, having never seen a response, resends the
		// identical Add under the SAME (still unadvanced) SyncKey -- exactly the resend F2 protects
		// against duplicating, except this time the first attempt definitely did NOT send anything.
		_harness.Session.Submit.FailWith = null;

		XElement? result = await handler.ApplyClientCommandAsync(
			context, drafts, _harness.Session.Store, CommandFor("c1"),
			new Dictionary<string, string>(StringComparer.Ordinal),
			new BodyPreference(1, null, false), deletesAsMoves: true, ClientCommandLedger.Empty(),
			syncKeyForClaim: 1, CancellationToken.None);

		Assert.Equal("1", result?.Element(AS + "Status")?.Value);
		// The resend must actually perform the send -- reporting success without ever calling
		// SendAsync would silently lose the user's mail while telling the client it was delivered.
		Assert.Single(_harness.Session.Submit.Sent);
	}

	// F2 (coverage): the Change/email2:Send seam (SmartReply-style draft edit-and-send) guards the
	// identical crash window with the identical mechanism (TryClaimSendAsync), keyed by ServerId
	// instead of ClientId. Not run through a full red-first cycle separately — the guard is the
	// same code path proven above for Add — but this exercises it end to end to confirm the Change
	// branch's claim key (and its "already gone" resend outcome) actually wires up correctly.
	[Fact]
	public async Task DraftChangeSubmit_ResentAfterACrashBeforeCommit_DoesNotSubmitTwice()
	{
		List<UserFolder> folders = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:Drafts", "Drafts", null, EasFolderType.Drafts, EasClass.Email));
		UserFolder drafts = folders.Single(f => f.BackendKey == "imap:Drafts");

		SyncHandler handler = NewSyncHandler();
		EasContext context = await _harness.NewContextAsync();
		string serverId = $"{drafts.ServerId}:99";
		_harness.Session.Mail.RawMessage = System.Text.Encoding.UTF8.GetBytes(
			"From: u@example.test\r\nTo: dest@example.com\r\nSubject: draft\r\n\r\ndraft body\r\n");

		XElement CommandFor() => new(AS + "Change",
			new XElement(AS + "ServerId", serverId),
			new XElement(AS + "ApplicationData",
				new XElement(Email + "To", "dest@example.com"),
				new XElement(Email + "Subject", "Hi"),
				new XElement(ASB + "Body",
					new XElement(ASB + "Type", "1"),
					new XElement(ASB + "Data", "hello there")),
				new XElement(E2 + "Send")));

		await handler.ApplyClientCommandAsync(
			context, drafts, _harness.Session.Store, CommandFor(),
			new Dictionary<string, string>(StringComparer.Ordinal),
			new BodyPreference(1, null, false), deletesAsMoves: true, ClientCommandLedger.Empty(),
			syncKeyForClaim: 1, CancellationToken.None);

		await handler.ApplyClientCommandAsync(
			context, drafts, _harness.Session.Store, CommandFor(),
			new Dictionary<string, string>(StringComparer.Ordinal),
			new BodyPreference(1, null, false), deletesAsMoves: true, ClientCommandLedger.Empty(),
			syncKeyForClaim: 1, CancellationToken.None);

		Assert.Single(_harness.Session.Submit.Sent);
	}

	private SyncHandler NewSyncHandler()
	{
		return new SyncHandler(
			_harness.Folders,
			TestOptionsMonitor.SnapshotOf(_harness.Options),
			new StubLifetime(),
			new MeetingInvitationService(NullLogger<MeetingInvitationService>.Instance),
			NullLogger<SyncHandler>.Instance);
	}

	private sealed class StubLifetime : IHostApplicationLifetime
	{
		public CancellationToken ApplicationStarted => CancellationToken.None;
		public CancellationToken ApplicationStopping => CancellationToken.None;
		public CancellationToken ApplicationStopped => CancellationToken.None;

		public void StopApplication()
		{
		}
	}
}
