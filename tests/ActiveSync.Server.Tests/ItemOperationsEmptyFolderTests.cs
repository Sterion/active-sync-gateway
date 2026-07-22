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
///   (6 unresolvable, 2 not a mail folder, 3 read-only/access-denied) rather than collapsing all
///   three to status 2, which leaves a client unable to tell "not permitted" from "not found".
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

	[Fact]
	public async Task ReadOnlyFolder_ReportsStatus3()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		_harness.Session.ReadOnlyBackendKeys.Add("imap:INBOX");

		XDocument? response = await RunAsync(registry.Single().ServerId);

		Assert.Equal("3", StatusOf(response));
		Assert.Empty(_harness.Session.Mail.Emptied);
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
