using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   F45 — EmptyFolderContents must report distinct statuses for its distinct failure causes
///   (6 unresolvable, 2 not a mail folder / read-only-blocked, 3 a genuine retryable backend
///   failure) rather than collapsing them. F11: the read-only/blocked case specifically must
///   answer the documented terminal status "2" (AGENTS.md's read-only scheme: "EmptyFolderContents/
///   MeetingResponse Status 2"), not "3" — "3" is reserved for F10's genuine backend failure, which
///   a client may legitimately retry; a client that retries a permanently-blocked bulk delete every
///   sync round against a gateway that will never allow it never sees a refusal.
/// </summary>
public sealed class ItemOperationsEmptyFolderTests : IDisposable
{
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	[Fact]
	public async Task UnresolvableCollection_ReportsStatus6()
	{
		XDocument? response = await RunAsync("imap:9999"); // never registered

		Assert.Equal("6", StatusOf(response));
		Assert.Empty(_harness.Session.Mail.Emptied);
	}

	[Fact]
	public async Task NonMailFolder_ReportsStatus2()
	{
		_harness.Session.SecondaryStore = new EasHandlerHarness.RecordingStore
		{
			EasClass = EasClass.Calendar, KeyPrefix = "caldav:"
		};
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("caldav:Cal", "Calendar", null, EasFolderType.Calendar, EasClass.Calendar));

		XDocument? response = await RunAsync(registry.Single().ServerId);

		Assert.Equal("2", StatusOf(response));
		Assert.Empty(_harness.Session.Mail.Emptied);
	}

	// F11 — read-only mode's documented scheme answers EmptyFolderContents with the terminal
	// status "2", not the retryable "3" a genuine backend failure gets (F10). Renamed from
	// ReadOnlyFolder_ReportsStatus3: that was the finding itself — the read-only/blocked case was
	// wrongly sharing "3" with a transient failure, a BEHAVIOUR CHANGE this test now asserts.
	[Fact]
	public async Task ReadOnlyFolder_ReportsStatus2()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		_harness.Session.ReadOnlyBackendKeys.Add("imap:INBOX");

		XDocument? response = await RunAsync(registry.Single().ServerId);

		Assert.Equal("2", StatusOf(response));
		Assert.Empty(_harness.Session.Mail.Emptied);
	}

	// F10 — a backend failure during EmptyFolderContents must fail just that one operation with a
	// retryable status, not escape as an unhandled exception that turns the whole ItemOperations
	// request into an HTTP 500 (the sibling Fetch already wraps its core for the same reason).
	[Fact]
	public async Task BackendFailure_ReportsRetryableStatus_InsteadOfThrowing()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		_harness.Session.Mail.EmptyFolderFailWith = () => new BackendException("backend blipped");

		XDocument? response = await RunAsync(registry.Single().ServerId);

		Assert.Equal("3", StatusOf(response));
	}

	[Fact]
	public async Task WritableMailFolder_IsEmptied()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));

		XDocument? response = await RunAsync(registry.Single().ServerId);

		Assert.Equal("1", StatusOf(response));
		Assert.Equal(["imap:INBOX"], _harness.Session.Mail.Emptied);
	}

	private string? StatusOf(XDocument? response)
	{
		return response?.Root?.Element(IO + "Response")?.Element(IO + "EmptyFolderContents")?
			.Element(IO + "Status")?.Value;
	}

	private Task<XDocument?> RunAsync(string collectionId)
	{
		return _harness.RunAsync(
			new ItemOperationsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<ItemOperationsHandler>.Instance),
			"ItemOperations",
			new XDocument(new XElement(IO + "ItemOperations",
				new XElement(IO + "EmptyFolderContents",
					new XElement(AS + "CollectionId", collectionId)))));
	}
}
