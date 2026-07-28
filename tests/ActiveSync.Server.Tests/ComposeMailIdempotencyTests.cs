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
///     <item>A referenced source that cannot be resolved must fail the command, never send a
///       degraded message (a forward with nothing forwarded).</item>
///     <item>A failure AFTER a successful submit (filing to Sent, flagging the source) must
///       not be reported as a send failure, or the client resends and duplicates the mail.</item>
///     <item>SmartReply/SmartForward must resolve the referenced source item ONCE per send:
///       <c>BuildOutgoingAsync</c> resolves it to build the quote/attachment, and
///       <c>MarkSourceAsync</c> must reuse that resolution to flag it, not resolve it again.</item>
///     <item>SendMail/SmartReply/SmartForward must dedup on <c>composemail:ClientId</c>: a
///       retried submit (the phone resending after it lost the 200) must not send the mail a second
///       time. A request with no ClientId (the 12.x raw form) always falls through to a real send.</item>
///     <item>SendMail-by-reference (Source with no Mime) must only hard-delete the source when
///       it is actually a Drafts item; a Source pointing at any other folder must be left untouched
///       after the send, not permanently deleted with no tombstone and no Trash copy.</item>
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

	private async Task<UserFolder> DraftsAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:Drafts", "Drafts", null, EasFolderType.Drafts, EasClass.Email));
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

	// SendMailHandler.MarkSourceAsync bypasses the source-resolution cache
	// ResolveSourceAsync introduced and its own base class documents: it
	// calls Folders.ResolveCollectionAsync/ResolveItemKeyAsync directly instead of ResolveSourceAsync,
	// a second DB round trip per send for no reason — the sibling handlers (below) do not do this.
	[Fact]
	public async Task SendMail_ResolvesSourceExactlyOnce()
	{
		UserFolder drafts = await DraftsAsync();
		_harness.Session.Mail.RawMessage = Encoding.UTF8.GetBytes(
			"From: u@example.test\r\nTo: dest@example.com\r\nSubject: draft\r\n\r\ndraft body\r\n");

		XDocument request = new(new XElement(CM + "SendMail",
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", drafts.ServerId),
				new XElement(CM + "ItemId", $"{drafts.ServerId}:99"))));

		SendMailHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		int before = _harness.FolderResolutionQueries;
		await _harness.RunAsync(handler, "SendMail", request);

		// BuildOutgoingAsync resolves the source to fetch the stored draft; MarkSourceAsync then
		// consumes (deletes) that same item — that must reuse the SAME resolution, not look it up
		// again.
		Assert.Equal(1, _harness.FolderResolutionQueries - before);
	}

	// BuildOutgoingAsync resolves the source item (to quote/attach it); MarkSourceAsync then
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
		// flags it "forwarded" — that must reuse the SAME resolution, not look it up again.
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

	// WritePermission.IsBlocked (the per-folder read-only/share grant) exists
	// precisely so "a handler cannot honour one write path and forget the other" (WritePermission.cs).
	// SmartReply/SmartForward only ever checked the GLOBAL ReadOnly flag before the send; the
	// post-send source-flagging write (SetAnsweredAsync) must also respect a per-folder read-only
	// grant, the same way SendMailHandler's post-send delete already does.
	[Fact]
	public async Task SmartReply_WithReadOnlyBlockedSource_DoesNotFlagTheSource()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(inbox.BackendKey); // a shared, read-only grant
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

		XDocument? response = await _harness.RunAsync(handler, "SmartReply", request);

		Assert.Null(response); // the reply itself is still sent — only the source flag is blocked
		Assert.Single(_harness.Session.Submit.Sent);
		Assert.Empty(_harness.Session.Mail.Answered);
	}

	[Fact]
	public async Task SmartForward_WithReadOnlyBlockedSource_DoesNotFlagTheSource()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(inbox.BackendKey);
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

		XDocument? response = await _harness.RunAsync(handler, "SmartForward", request);

		Assert.Null(response);
		Assert.Single(_harness.Session.Submit.Sent);
		Assert.Empty(_harness.Session.Mail.Answered);
	}

	// MS-ASCMD makes ClientId a required child of SendMail precisely so a lost 200 can be
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

	// The 12.x raw wire form (message/rfc822 body, no WBXML ClientId element at all) has
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

	// A Source pointing into a NON-Drafts folder (e.g. the Inbox) must be re-sent but NOT
	// hard-deleted: nothing enforces the 16.x "submit a stored draft" flow's assumption that Source
	// names a draft, so a client (or a bug) pointing SendMail at an ordinary message must not lose
	// it with no tombstone and no Trash copy.
	[Fact]
	public async Task SendMailByReference_FromNonDraftsFolder_DoesNotDeleteTheSource()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.RawMessage = Encoding.UTF8.GetBytes(
			"From: sender@example.test\r\nTo: u@example.test\r\nSubject: original\r\n\r\noriginal body\r\n");

		XDocument request = new(new XElement(CM + "SendMail",
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", inbox.ServerId),
				new XElement(CM + "ItemId", $"{inbox.ServerId}:42"))));

		SendMailHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		XDocument? response = await _harness.RunAsync(handler, "SendMail", request);

		Assert.Null(response); // success = empty 200 — the message DOES still go out
		Assert.Single(_harness.Session.Submit.Sent);
		// …but the source, which is NOT a draft, must survive untouched.
		Assert.Empty(_harness.Session.Store.Deleted);
	}

	// Companion: the legitimate 16.x "submit a stored draft" flow (Source names an item actually IN
	// Drafts) must keep consuming the draft — the fix must not regress this.
	[Fact]
	public async Task SendMailByReference_FromDraftsFolder_StillDeletesTheSource()
	{
		UserFolder drafts = await DraftsAsync();
		_harness.Session.Mail.RawMessage = Encoding.UTF8.GetBytes(
			"From: u@example.test\r\nTo: dest@example.com\r\nSubject: draft\r\n\r\ndraft body\r\n");

		XDocument request = new(new XElement(CM + "SendMail",
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", drafts.ServerId),
				new XElement(CM + "ItemId", $"{drafts.ServerId}:99"))));

		SendMailHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		XDocument? response = await _harness.RunAsync(handler, "SendMail", request);

		Assert.Null(response);
		Assert.Single(_harness.Session.Submit.Sent);
		Assert.Equal(["imap:Drafts/99"], _harness.Session.Store.Deleted);
	}

	private static XElement OpaqueMime(string mime)
	{
		XElement element = new(CM + "Mime", Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)));
		element.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		return element;
	}
}
