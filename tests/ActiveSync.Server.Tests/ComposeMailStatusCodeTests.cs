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
///   ComposeMail (SendMail / SmartReply / SmartForward) status-code conformance:
///   an undecodable/empty-MIME send is answered with the MS-ASCMD common status 107 (InvalidMIME),
///   not 103 (InvalidXML, which is reserved for a genuine request-parse failure).
/// </summary>
public sealed class ComposeMailStatusCodeTests : IDisposable
{
	private static readonly XNamespace CM = EasNamespaces.ComposeMail;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	// A request that decoded fine but carries neither MIME, Forwardees nor a Source is not an
	// XML problem; it is an empty/invalid MIME submission.
	[Fact]
	public async Task SendMail_WithNoMimeNoForwardeesNoSource_ReportsInvalidMimeNotInvalidXml()
	{
		XDocument request = new(new XElement(CM + "SendMail"));

		SendMailHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		XDocument? response = await _harness.RunAsync(handler, "SendMail", request);

		Assert.NotNull(response);
		Assert.Equal("SendMail", response!.Root!.Name.LocalName);
		Assert.Equal("107", response.Root.Element(CM + "Status")?.Value);
	}

	// SendMail-by-reference (Source with no Mime) whose draft cannot be resolved yields empty
	// outgoing bytes; that is also an empty-MIME submission, not a request-parse failure.
	[Fact]
	public async Task SendMail_WithUnresolvableDraftReference_ReportsInvalidMimeNotInvalidXml()
	{
		XDocument request = new(new XElement(CM + "SendMail",
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", "999"),
				new XElement(CM + "ItemId", "999:1"))));

		SendMailHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		XDocument? response = await _harness.RunAsync(handler, "SendMail", request);

		Assert.NotNull(response);
		Assert.Equal("107", response!.Root!.Element(CM + "Status")?.Value);
		Assert.Empty(_harness.Session.Submit.Sent);
	}
}
