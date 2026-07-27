using System.Text;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   ComposeMail (SendMail / SmartReply / SmartForward) send-then-fail semantics:
///   <list type="bullet">
///     <item>F29 — a referenced source that cannot be resolved must fail the command, never send a
///       degraded message (a forward with nothing forwarded).</item>
///     <item>F30 — a failure AFTER a successful submit (filing to Sent, flagging the source) must
///       not be reported as a send failure, or the client resends and duplicates the mail.</item>
///     <item>F4 — SmartReply/SmartForward must resolve the referenced source item ONCE per send:
///       <c>BuildOutgoingAsync</c> resolves it to build the quote/attachment, and
///       <c>MarkSourceAsync</c> must reuse that resolution to flag it, not resolve it again.</item>
///     <item>F1 — SendMail/SmartReply/SmartForward must dedup on <c>composemail:ClientId</c>: a
///       retried submit (the phone resending after it lost the 200) must not send the mail a second
///       time. A request with no ClientId (the 12.x raw form) always falls through to a real send.</item>
///   </list>
/// </summary>
public sealed class ComposeMailIdempotencyTests : IDisposable
{
	private static readonly XNamespace CM = EasNamespaces.ComposeMail;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	private async Task<UserFolder> InboxAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return registry.Single();
	}

	[Fact]
	public async Task SmartForward_WithUnresolvableSource_FailsWithoutSending()
	{
		// A Source that points at a collection the user does not have — a stale ServerId.
		XDocument request = new(new XElement(CM + "SmartForward",
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", "999"),
				new XElement(CM + "ItemId", "999:1")),
			OpaqueMime("From: u@example.test\r\nTo: dest@example.com\r\nSubject: fwd\r\n\r\nhi\r\n")));

		SmartForwardHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SmartForwardHandler>.Instance);

		XDocument? response = await _harness.RunAsync(handler, "SmartForward", request);

		// Not an empty 200 (which is success) — a real failure status…
		Assert.NotNull(response);
		Assert.Equal("SmartForward", response!.Root!.Name.LocalName);
		Assert.Equal("150", response.Root.Element(CM + "Status")?.Value);
		// …and nothing was submitted.
		Assert.Empty(_harness.Session.Submit.Sent);
	}

	[Fact]
	public async Task SendMail_WhenFilingToSentFails_ReportsSuccessAndDoesNotResend()
	{
		// The submit succeeds; filing to Sent — a best-effort follow-up — fails.
		_harness.Session.Mail.SaveToSentShouldThrow = true;

		XDocument request = new(new XElement(CM + "SendMail",
			new XElement(CM + "SaveInSentItems"),
			OpaqueMime("From: u@example.test\r\nTo: dest@example.com\r\nSubject: hi\r\n\r\nbody\r\n")));

		SendMailHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		XDocument? response = await _harness.RunAsync(handler, "SendMail", request);

		// Success for SendMail is an empty 200 — not the Status 120 the old single catch emitted.
		Assert.Null(response);
		// The mail went out exactly once, and the file-to-Sent failure was reached and swallowed.
		Assert.Single(_harness.Session.Submit.Sent);
		Assert.True(_harness.Session.Mail.SaveToSentAttempted);
	}

	// F4 — BuildOutgoingAsync resolves the source item (to quote/attach it); MarkSourceAsync then
	// flags that same item (answered/forwarded). It must reuse the first resolution rather than
	// resolving the ServerId a second time.
	[Fact]
	public async Task SmartForward_ResolvesSourceExactlyOnce()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.RawMessage = Encoding.UTF8.GetBytes(
			"From: sender@example.test\r\nTo: u@example.test\r\nSubject: original\r\n\r\noriginal body\r\n");

		XDocument request = new(new XElement(CM + "SmartForward",
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", inbox.ServerId),
				new XElement(CM + "ItemId", $"{inbox.ServerId}:42")),
			OpaqueMime("From: u@example.test\r\nTo: dest@example.com\r\nSubject: fwd\r\n\r\nsee below\r\n")));

		SmartForwardHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SmartForwardHandler>.Instance);

		int before = _harness.FolderResolutionQueries;
		await _harness.RunAsync(handler, "SmartForward", request);

		// BuildOutgoingAsync resolves the source to fetch+attach the original; MarkSourceAsync then
		// flags it "forwarded" — that must reuse the SAME resolution, not look it up again (F4).
		Assert.Equal(1, _harness.FolderResolutionQueries - before);
	}

	[Fact]
	public async Task SmartReply_ResolvesSourceExactlyOnce()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.RawMessage = Encoding.UTF8.GetBytes(
			"From: sender@example.test\r\nTo: u@example.test\r\nSubject: original\r\n\r\noriginal body\r\n");

		XDocument request = new(new XElement(CM + "SmartReply",
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", inbox.ServerId),
				new XElement(CM + "ItemId", $"{inbox.ServerId}:42")),
			OpaqueMime("From: u@example.test\r\nTo: sender@example.test\r\nSubject: re: original\r\n\r\nmy reply\r\n")));

		SmartReplyHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SmartReplyHandler>.Instance);

		int before = _harness.FolderResolutionQueries;
		await _harness.RunAsync(handler, "SmartReply", request);

		Assert.Equal(1, _harness.FolderResolutionQueries - before);
	}

	// F1 — MS-ASCMD makes ClientId a required child of SendMail precisely so a lost 200 can be
	// retried without duplicating the mail: the server recognizes the resend and suppresses it.
	[Fact]
	public async Task SendMail_RetriedWithSameClientId_SendsOnlyOnce()
	{
		XDocument RequestWithClientId() => new(new XElement(CM + "SendMail",
			new XElement(CM + "ClientId", "phone-cid-1"),
			OpaqueMime("From: u@example.test\r\nTo: dest@example.com\r\nSubject: hi\r\n\r\nbody\r\n")));

		SendMailHandler NewHandler() => new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		// Attempt 1: the mail goes out, but (in this scenario) the phone never sees the 200 —
		// Wi-Fi→LTE handover, a proxy timeout, the gateway restarting between SendAsync and the
		// response write.
		XDocument? first = await _harness.RunAsync(NewHandler(), "SendMail", RequestWithClientId());
		Assert.Null(first); // success = empty 200

		// Attempt 2: the phone resends the IDENTICAL request (same ClientId) because it never saw
		// attempt 1's response. The recipient must NOT receive the message twice.
		XDocument? second = await _harness.RunAsync(NewHandler(), "SendMail", RequestWithClientId());
		Assert.Null(second); // still reports success — the client must not treat the resend as a failure

		Assert.Single(_harness.Session.Submit.Sent);
	}

	// F1 — the 12.x raw wire form (message/rfc822 body, no WBXML ClientId element at all) has
	// nothing to dedup on; it must keep sending normally rather than being silently swallowed.
	[Fact]
	public async Task SendMail_WithoutClientId_AlwaysSends()
	{
		XDocument RequestWithoutClientId() => new(new XElement(CM + "SendMail",
			OpaqueMime("From: u@example.test\r\nTo: dest@example.com\r\nSubject: hi\r\n\r\nbody\r\n")));

		SendMailHandler NewHandler() => new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		await _harness.RunAsync(NewHandler(), "SendMail", RequestWithoutClientId());
		await _harness.RunAsync(NewHandler(), "SendMail", RequestWithoutClientId());

		Assert.Equal(2, _harness.Session.Submit.Sent.Count);
	}

	private static XElement OpaqueMime(string mime)
	{
		XElement element = new(CM + "Mime", Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)));
		element.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		return element;
	}
}
