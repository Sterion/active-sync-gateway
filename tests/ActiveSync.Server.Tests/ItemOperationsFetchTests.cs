using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   ItemOperations Fetch by LongId (the handle Search results carry) resolves a store
///   straight from the backend key inside the client-supplied id. The folder registry — the
///   only thing that says which folders belong to this user — was never consulted, so a
///   LongId naming any key the store recognizes was honoured.
/// </summary>
public sealed class ItemOperationsFetchTests : IDisposable
{
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;
	private static readonly XNamespace SE = EasNamespaces.Search;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	// A 16.x client fetching an item outside Sync (ItemOperations Fetch by CollectionId/
	// ServerId) must get the SAME version-gated BodyPreference.Eas16 flag Sync itself computes
	// (context.Version >= EasVersion.V160), not a hard-coded false. Without it, 16.x-only shapes
	// (airsyncbase:Location, event attachments) silently disappear from a bare Fetch.
	//
	// The preference is HOST-side now (a store never sees one), so the gate is asserted where it
	// is decided — the fetch itself is still driven end-to-end below to prove the path works.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Fetch_BodyPreference_CarriesTheVersionGate(bool eas16)
	{
		BodyPreference withoutOptions = ItemOperationsHandler.ParseBodyPreference(null, eas16);
		BodyPreference withOptions = ItemOperationsHandler.ParseBodyPreference(
			new XElement(IO + "Options",
				new XElement(EasNamespaces.AirSyncBase + "BodyPreference",
					new XElement(EasNamespaces.AirSyncBase + "Type", "1"))), eas16);

		Assert.Equal(eas16, withoutOptions.Eas16);
		Assert.Equal(eas16, withOptions.Eas16);
	}

	[Fact]
	public async Task Fetch_ByCollectionId_Eas16Client_Succeeds()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			EasHandlerHarness.Folder("imap:INBOX", "Inbox", FolderType.Inbox, EasClass.Email));
		UserFolder inbox = registry.Single();

		XDocument? response = await _harness.RunAsync(
			new ItemOperationsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<ItemOperationsHandler>.Instance),
			"ItemOperations",
			new XDocument(new XElement(IO + "ItemOperations",
				new XElement(IO + "Fetch",
					new XElement(AS + "CollectionId", inbox.ServerId),
					new XElement(AS + "ServerId", $"{inbox.ServerId}:1")))),
			protocolVersion: "16.1");

		Assert.Equal("1",
			response?.Root?.Element(IO + "Response")?.Element(IO + "Fetch")?.Element(IO + "Status")?.Value);
		Assert.Equal([$"imap:INBOX/1"], _harness.Session.Store.Fetched);
	}

	[Fact]
	public async Task LongIdFetch_ForAFolderOutsideTheRegistry_IsRefused()
	{
		await RegisterInboxAsync();

		// "imap:Someone-Else" is never in this user's registry, but the store recognizes the
		// key shape — which used to be the entire check.
		XDocument? response = null;
		try
		{
			response = await FetchLongIdAsync(DelimitedKey.Encode("imap:Someone-Else", "1"));
		}
		catch (WbxmlException)
		{
			// Getting far enough to encode a SUCCESS response is itself the defect; the
			// assertion below is the one that names it.
		}

		Assert.Empty(_harness.Session.Store.Fetched);
		XElement? fetch = response?.Root?.Element(IO + "Response")?.Element(IO + "Fetch");
		Assert.Equal("6", fetch?.Element(IO + "Status")?.Value);
	}

	[Fact]
	public async Task LongIdFetch_ForARegisteredFolder_StillReachesTheStore()
	{
		await RegisterInboxAsync();

		// The control for the refusal above. It asserts the handler's DECISION rather than
		// its response, because the success response is currently unencodable: the
		// ItemOperations code page has no LongId tag (it lives on Search and ComposeMail only),
		// so `<itemoperations:LongId>` throws encoding the response on the way out. That is a
		// separate, known defect, deliberately not fixed here; when it is, this can assert Status 1.
		await Assert.ThrowsAsync<WbxmlException>(() =>
			FetchLongIdAsync(DelimitedKey.Encode("imap:INBOX", "1")));

		Assert.Equal(["imap:INBOX/1"], _harness.Session.Store.Fetched);
	}

	private Task RegisterInboxAsync()
	{
		return _harness.RegisterFoldersAsync(
			EasHandlerHarness.Folder("imap:INBOX", "Inbox", FolderType.Inbox, EasClass.Email));
	}

	private Task<XDocument?> FetchLongIdAsync(string longId)
	{
		// Real clients send search:LongId — the tag only exists on the Search code page.
		return _harness.RunAsync(
			new ItemOperationsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<ItemOperationsHandler>.Instance),
			"ItemOperations",
			new XDocument(new XElement(IO + "ItemOperations",
				new XElement(IO + "Fetch",
					new XElement(SE + "LongId", longId)))));
	}
}
