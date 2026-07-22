using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   GetItemEstimate (MS-ASCMD 2.2.1.9) status-code conformance: an unprimed/invalid sync key
///   is status 3 (re-sync from 0), not 4 (F19); a flaky backend during the revision listing
///   degrades that one collection to status 2 rather than 500-ing the whole request (F20).
/// </summary>
public sealed class GetItemEstimateTests : IDisposable
{
	private static readonly XNamespace GIE = EasNamespaces.GetItemEstimate;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	// F19 — a stale/unprimed sync key must be status 3 ("invalid sync key, re-sync from 0"),
	// not 4 ("collection invalid"), which makes the client drop the folder from the hierarchy.
	[Fact]
	public async Task InvalidSyncKey_ReportsStatus3()
	{
		UserFolder inbox = await InboxAsync();

		XDocument? response = await RunAsync(inbox.ServerId, "5"); // nonzero key, no primed state

		XElement? resp = response?.Root?.Element(GIE + "Response");
		Assert.Equal("3", resp?.Element(GIE + "Status")?.Value);
	}

	// F20 — a single flaky store must not take down the multi-collection request. The endpoint
	// has a catch-all that would turn an unguarded throw into HTTP 500; the handler must catch it
	// and report status 2 for that collection instead (matching SyncHandler's per-collection path).
	[Fact]
	public async Task BackendListingFailure_ReportsStatus2_DoesNotThrow()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Store.GetRevisionsFailWith = () => new BackendException("listing blew up");

		XDocument? response = await RunAsync(inbox.ServerId, "0"); // initial → reaches the listing

		XElement? resp = response?.Root?.Element(GIE + "Response");
		Assert.Equal("2", resp?.Element(GIE + "Status")?.Value);
	}

	private async Task<UserFolder> InboxAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return registry.Single();
	}

	private Task<XDocument?> RunAsync(string collectionId, string syncKey)
	{
		return _harness.RunAsync(
			new GetItemEstimateHandler(_harness.Folders, NullLogger<GetItemEstimateHandler>.Instance),
			"GetItemEstimate",
			new XDocument(new XElement(GIE + "GetItemEstimate",
				new XElement(GIE + "Collections",
					new XElement(GIE + "Collection",
						new XElement(AS + "SyncKey", syncKey),
						new XElement(AS + "CollectionId", collectionId))))));
	}
}
