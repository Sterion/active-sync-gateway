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
			new BodyPreference(1, null, false), deletesAsMoves: true, ledger, CancellationToken.None);

		// A successful submission is reported as success, not the Status 6 the catch would emit.
		Assert.Equal("1", result?.Element(AS + "Status")?.Value);
		// It went out exactly once…
		Assert.Single(_harness.Session.Submit.Sent);
		// …the best-effort file to Sent was reached (and swallowed)…
		Assert.True(_harness.Session.Mail.SaveToSentAttempted);
		// …and the replay marker is recorded, so a client resend replays instead of re-sending.
		Assert.True(ledger.AppliedAdds.ContainsKey("c1"));
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
