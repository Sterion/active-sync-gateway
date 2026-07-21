using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   F46 — ItemOperations Fetch by LongId (the handle Search results carry) resolves a store
///   straight from the backend key inside the client-supplied id. The folder registry — the
///   only thing that says which folders belong to this user — was never consulted, so a
///   LongId naming any key the store recognizes was honoured.
/// </summary>
public sealed class ItemOperationsFetchTests : IDisposable
{
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;
	private static readonly XNamespace SE = EasNamespaces.Search;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
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
		// ItemOperations code page has no LongId tag, so `<itemoperations:LongId>` throws on
		// the way out. That is a separate defect (filed at the bottom of Part 2 of the
		// review) and deliberately not fixed here; when it is, this can assert Status 1.
		await Assert.ThrowsAsync<WbxmlException>(() =>
			FetchLongIdAsync(DelimitedKey.Encode("imap:INBOX", "1")));

		Assert.Equal(["imap:INBOX/1"], _harness.Session.Store.Fetched);
	}

	private Task RegisterInboxAsync()
	{
		return _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
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
