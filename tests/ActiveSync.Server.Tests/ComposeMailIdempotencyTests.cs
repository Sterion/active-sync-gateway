using System.Text;
using System.Xml.Linq;
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

	private static XElement OpaqueMime(string mime)
	{
		XElement element = new(CM + "Mime", Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)));
		element.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		return element;
	}
}
