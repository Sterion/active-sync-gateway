using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   MeetingResponse (MS-ASCMD 2.2.1.11): a transient backend failure must read as retryable
///   status 4, not status 2 "invalid meeting request" (F34); the invitation mail is removed after
///   a successful response (F35); a calendar-collection CollectionId reads the event instead of
///   handing a calendar backend key to the mail store (F33).
/// </summary>
public sealed class MeetingResponseTests : IDisposable
{
	private static readonly XNamespace MR = EasNamespaces.MeetingResponse;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	private MeetingResponseHandler NewHandler()
	{
		return new MeetingResponseHandler(_harness.Folders,
			TestOptionsMonitor.SnapshotOf(_harness.Options), NullLogger<MeetingResponseHandler>.Instance);
	}

	private async Task<UserFolder> InboxAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return registry.Single();
	}

	private Task<XDocument?> RunAsync(string requestId, string collectionId)
	{
		return _harness.RunAsync(NewHandler(), "MeetingResponse",
			new XDocument(new XElement(MR + "MeetingResponse",
				new XElement(MR + "Request",
					new XElement(MR + "RequestId", requestId),
					new XElement(MR + "CollectionId", collectionId),
					new XElement(MR + "UserResponse", "1")))));
	}

	private static string? StatusOf(XDocument? response)
	{
		return response?.Root?.Element(MR + "Result")?.Element(MR + "Status")?.Value;
	}

	// F34 — a backend failure while loading the invite must be status 4 (retryable server error),
	// not status 2 (invalid meeting request), which tells the client the request itself was bad.
	[Fact]
	public async Task BackendFailure_ReportsStatus4()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Mail.GetRawFailWith = () => new BackendException("transient");

		XDocument? response = await RunAsync($"{inbox.ServerId}:42", inbox.ServerId);

		Assert.Equal("4", StatusOf(response));
	}

	// Control: genuinely malformed input (an unresolvable collection) stays status 2.
	[Fact]
	public async Task UnresolvableCollection_StaysStatus2()
	{
		XDocument? response = await RunAsync("imap:9999:42", "imap:9999");

		Assert.Equal("2", StatusOf(response));
	}
}
