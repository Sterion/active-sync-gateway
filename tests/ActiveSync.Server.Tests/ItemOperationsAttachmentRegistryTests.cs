using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Http;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   A mail-attachment FileReference ("{imapBackendKey}|{uid}|{attachmentIndex}")
///   is client-supplied and names a backend folder directly, exactly like the Search LongId
///   (see ItemOperationsFetchTests) already guards — but the FileReference path skipped the same
///   per-user folder-registry check, in both ItemOperations Fetch and the legacy GetAttachment
///   command.
/// </summary>
public sealed class ItemOperationsAttachmentRegistryTests : IDisposable
{
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	[Fact]
	public async Task ItemOperationsFetch_ForAFolderOutsideTheRegistry_IsRefused()
	{
		await RegisterInboxAsync();
		// The backend WOULD happily answer — proving the gate, not a coincidental null result.
		_harness.Session.Mail.RawMessage = MessageWithAttachment("secret");

		string fileReference = DelimitedKey.Encode("imap:Someone-Else", "42", "0");
		XDocument? response = await _harness.RunAsync(
			new ItemOperationsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<ItemOperationsHandler>.Instance),
			"ItemOperations",
			new XDocument(new XElement(IO + "ItemOperations",
				new XElement(IO + "Fetch",
					new XElement(ASB + "FileReference", fileReference)))));

		XElement? fetch = response?.Root?.Element(IO + "Response")?.Element(IO + "Fetch");
		Assert.Equal("6", fetch?.Element(IO + "Status")?.Value);
		Assert.Equal(0, _harness.Session.Mail.GetRawMessageCalls);
	}

	[Fact]
	public async Task ItemOperationsFetch_ForARegisteredFolder_StillReachesTheStore()
	{
		await RegisterInboxAsync();
		_harness.Session.Mail.RawMessage = MessageWithAttachment("hello");

		string fileReference = DelimitedKey.Encode("imap:INBOX", "42", "0");
		XDocument? response = await _harness.RunAsync(
			new ItemOperationsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<ItemOperationsHandler>.Instance),
			"ItemOperations",
			new XDocument(new XElement(IO + "ItemOperations",
				new XElement(IO + "Fetch",
					new XElement(ASB + "FileReference", fileReference)))));

		XElement? fetch = response?.Root?.Element(IO + "Response")?.Element(IO + "Fetch");
		Assert.Equal("1", fetch?.Element(IO + "Status")?.Value);
		Assert.Equal(1, _harness.Session.Mail.GetRawMessageCalls);
	}

	[Fact]
	public async Task GetAttachment_ForAFolderOutsideTheRegistry_IsRefused()
	{
		await RegisterInboxAsync();
		_harness.Session.Mail.RawMessage = MessageWithAttachment("secret");

		DefaultHttpContext http = new();
		http.Response.Body = new MemoryStream();
		Device device = await _harness.State.GetOrCreateDeviceAsync(
			_harness.UserId, "TESTDEVICE01", "TestClient", CancellationToken.None);
		EasContext context = new()
		{
			Http = http,
			Parameters = new EasRequestParameters
			{
				Command = "GetAttachment",
				DeviceId = device.DeviceId,
				AttachmentName = DelimitedKey.Encode("imap:Someone-Else", "42", "0")
			},
			Credentials = new BackendCredentials { UserName = EasHandlerHarness.UserName, Password = "pw" },
			Session = _harness.Session,
			Device = device,
			State = _harness.State,
			WireLogger = NullLogger.Instance
		};

		GetAttachmentHandler handler = new();
		await handler.HandleAsync(context, CancellationToken.None);

		Assert.Equal(StatusCodes.Status404NotFound, context.Http.Response.StatusCode);
		Assert.Equal(0, _harness.Session.Mail.GetRawMessageCalls);
	}

	/// <summary>
	///   A message carrying exactly one attachment. The host extracts the part from the RAW
	///   message itself now (the store-side attachment fetch is gone), so proving the backend WOULD
	///   have answered means seeding real MIME rather than a canned <c>BackendAttachment</c>.
	/// </summary>
	private static byte[] MessageWithAttachment(string content)
	{
		string mime = string.Join("\r\n",
			"From: sender@example.test",
			"To: u@example.test",
			"Subject: with attachment",
			"Date: Mon, 1 Jul 2024 10:00:00 +0000",
			"Content-Type: multipart/mixed; boundary=\"b1\"",
			"",
			"--b1",
			"Content-Type: text/plain; charset=utf-8",
			"",
			"body",
			"--b1",
			"Content-Type: text/plain; charset=utf-8",
			"Content-Disposition: attachment; filename=\"note.txt\"",
			"",
			content,
			"--b1--",
			"");
		return System.Text.Encoding.ASCII.GetBytes(mime);
	}

	private Task RegisterInboxAsync()
	{
		return _harness.RegisterFoldersAsync(
			EasHandlerHarness.Folder("imap:INBOX", "Inbox", FolderType.Inbox, EasClass.Email));
	}
}
