using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Search (2.2.1.16) and Find (2.2.1.2) paging/shape conformance: Total reports the number of
///   matches, not the served page size; Find omits Range when empty and orders its
///   Result children per spec; paging past the fetch cap is refused without a backend call.
/// </summary>
public sealed class SearchFindConformanceTests : IDisposable
{
	private static readonly XNamespace S = EasNamespaces.Search;
	private static readonly XNamespace F = EasNamespaces.Find;
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	private readonly EasHandlerHarness _harness = new();

	public SearchFindConformanceTests()
	{
	}

	public void Dispose()
	{
		_harness.Dispose();
	}

	private SearchHandler NewSearch()
	{
		return new SearchHandler(_harness.Folders, NullLogger<SearchHandler>.Instance);
	}

	private FindHandler NewFind()
	{
		return new FindHandler(_harness.Folders, NullLogger<FindHandler>.Instance);
	}

	private async Task<UserFolder> InboxAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			EasHandlerHarness.Folder("imap:INBOX", "Inbox", FolderType.Inbox, EasClass.Email));
		return registry.Single();
	}

	private void SeedHits(int count)
	{
		for (int i = 1; i <= count; i++)
			_harness.Session.Mail.SearchHits.Add(("imap:INBOX", i.ToString()));
	}

	// Search Total must report the number of matches found, not the served page size. With
	// 4 hits and a page of 2 at offset 2, the served page is 2 but Total must reflect the 4 found.
	[Fact]
	public async Task Search_Total_ReportsHitCount_NotPageSize()
	{
		await InboxAsync();
		SeedHits(4);

		XDocument? response = await _harness.RunAsync(NewSearch(), "Search",
			new XDocument(new XElement(S + "Search",
				new XElement(S + "Store",
					new XElement(S + "Name", "Mailbox"),
					new XElement(S + "Query", new XElement(S + "FreeText", "hello")),
					new XElement(S + "Options", new XElement(S + "Range", "2-3"))))));

		XElement? store = response?.Root?.Element(S + "Response")?.Element(S + "Store");
		Assert.Equal("4", store?.Element(S + "Total")?.Value);
	}

	// A Search page of several same-folder hits is fetched in ONE batched call, not a
	// sequential GetItemAsync per hit.
	[Fact]
	public async Task Search_FetchesThePageInOneBatch_NotPerHit()
	{
		await InboxAsync();
		SeedHits(3);

		XDocument? response = await _harness.RunAsync(NewSearch(), "Search",
			new XDocument(new XElement(S + "Search",
				new XElement(S + "Store",
					new XElement(S + "Name", "Mailbox"),
					new XElement(S + "Query", new XElement(S + "FreeText", "hello")),
					new XElement(S + "Options", new XElement(S + "Range", "0-2"))))));

		Assert.Equal(3, response?.Root?.Element(S + "Response")?.Element(S + "Store")?
			.Elements(S + "Result").Count());
		// One batched fetch covering the whole page's three keys.
		Assert.Single(_harness.Session.Store.BatchFetched);
		Assert.Equal(["1", "2", "3"], _harness.Session.Store.BatchFetched[0].OrderBy(k => k));
	}

	// Find Total must not overreport past the end of the hit set. With 3 hits, a request for
	// offset 5 serves nothing; Total must be the 3 found, not start (5).
	[Fact]
	public async Task Find_Total_ReportsHitCount_NotStartPlusServed()
	{
		await InboxAsync();
		SeedHits(3);

		XDocument? response = await _harness.RunAsync(NewFind(), "Find",
			new XDocument(new XElement(F + "Find",
				new XElement(F + "SearchId", "x"),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "MailBoxSearchCriterion",
						new XElement(F + "Query", new XElement(F + "FreeText", "hello")),
						new XElement(F + "Options", new XElement(F + "Range", "5-6")))))));

		XElement? resp = response?.Root?.Element(F + "Response");
		Assert.Equal("3", resp?.Element(F + "Total")?.Value);
	}

	// Find must omit Range when there are no results; "0-0" claims one result was returned.
	[Fact]
	public async Task Find_NoResults_OmitsRange()
	{
		await InboxAsync();
		// No hits seeded.

		XDocument? response = await _harness.RunAsync(NewFind(), "Find",
			new XDocument(new XElement(F + "Find",
				new XElement(F + "SearchId", "x"),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "MailBoxSearchCriterion",
						new XElement(F + "Query", new XElement(F + "FreeText", "nothing")),
						new XElement(F + "Options", new XElement(F + "Range", "0-24")))))));

		XElement? resp = response?.Root?.Element(F + "Response");
		Assert.Null(resp?.Element(F + "Range"));
	}

	// Find Result child order must be Class, ServerId, CollectionId, Properties (MS-ASCMD),
	// not the ServerId, CollectionId, Class, Properties the double-AddFirst produced.
	[Fact]
	public async Task Find_ResultChildOrder_MatchesSpec()
	{
		UserFolder inbox = await InboxAsync();
		SeedHits(1);

		XDocument? response = await _harness.RunAsync(NewFind(), "Find",
			new XDocument(new XElement(F + "Find",
				new XElement(F + "SearchId", "x"),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "MailBoxSearchCriterion",
						new XElement(F + "Query",
							new XElement(F + "FreeText", "hello"),
							new XElement(AS + "CollectionId", inbox.ServerId)),
						new XElement(F + "Options", new XElement(F + "Range", "0-0")))))));

		XElement? result = response?.Root?.Element(F + "Response")?.Element(F + "Result");
		Assert.NotNull(result);
		string[] order = result.Elements().Select(e => e.Name.LocalName).ToArray();
		Assert.Equal(new[] { "Class", "ServerId", "CollectionId", "Properties" }, order);
	}

	// A mailbox-wide Find (no CollectionId narrowing the search, or DeepTraversal) must still
	// emit ServerId/CollectionId on every Result, resolved per-hit from the folder registry, not
	// only when a single CollectionId happened to scope the search. Without them the client has
	// nothing to hand to ItemOperations/Sync and the result cannot be opened.
	[Fact]
	public async Task Find_MailboxWide_ResultsCarryServerIdAndCollectionId_ResolvedPerHit()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			EasHandlerHarness.Folder("imap:INBOX", "Inbox", FolderType.Inbox, EasClass.Email),
			EasHandlerHarness.Folder("imap:Archive", "Archive", FolderType.UserMail, EasClass.Email));
		UserFolder inbox = registry.Single(f => f.BackendKey == "imap:INBOX");
		UserFolder archive = registry.Single(f => f.BackendKey == "imap:Archive");

		_harness.Session.Mail.SearchHits.Add(("imap:INBOX", "1"));
		_harness.Session.Mail.SearchHits.Add(("imap:Archive", "2"));

		// No CollectionId in the query — a mailbox-wide Find from the phone's search box.
		XDocument? response = await _harness.RunAsync(NewFind(), "Find",
			new XDocument(new XElement(F + "Find",
				new XElement(F + "SearchId", "x"),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "MailBoxSearchCriterion",
						new XElement(F + "Query", new XElement(F + "FreeText", "hello")),
						new XElement(F + "Options", new XElement(F + "Range", "0-1")))))));

		List<XElement> results = response?.Root?.Element(F + "Response")?.Elements(F + "Result").ToList() ?? [];
		Assert.Equal(2, results.Count);
		foreach (XElement result in results)
		{
			Assert.False(string.IsNullOrEmpty(result.Element(AS + "ServerId")?.Value));
			Assert.False(string.IsNullOrEmpty(result.Element(AS + "CollectionId")?.Value));
		}
		Assert.Equal(inbox.ServerId, results[0].Element(AS + "CollectionId")?.Value);
		Assert.Equal(archive.ServerId, results[1].Element(AS + "CollectionId")?.Value);
	}

	// A transient backend failure during Find must answer the retryable server-error status
	// (matching Search's own "3"), not the SAME terminal "2" a malformed request gets — otherwise a
	// phone whose IMAP server blipped is told its request was invalid and never retries.
	[Fact]
	public async Task Find_BackendFailure_ReportsRetryableStatus_NotProtocolError()
	{
		await InboxAsync();
		_harness.Session.Mail.SearchFailWith = () => new BackendException("backend blipped");

		XDocument? response = await _harness.RunAsync(NewFind(), "Find",
			new XDocument(new XElement(F + "Find",
				new XElement(F + "SearchId", "x"),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "MailBoxSearchCriterion",
						new XElement(F + "Query", new XElement(F + "FreeText", "hello")),
						new XElement(F + "Options", new XElement(F + "Range", "0-24")))))));

		Assert.Equal("3", response?.Root?.Element(F + "Status")?.Value);
	}

	// A 16.x client's mailbox Search must carry the same version-gated BodyPreference.Eas16
	// flag Sync computes, not a hard-coded false — otherwise a 16.x-only shape silently disappears
	// from Search results the same way it would from a bare ItemOperations Fetch. The preference
	// is HOST-side now (a store never sees one), so the gate is asserted where it is decided.
	[Theory]
	[InlineData("16.1", true)]
	[InlineData("16.0", true)]
	[InlineData("14.1", false)]
	public void Search_PreviewBodyPreference_CarriesTheVersionGate(string version, bool expected)
	{
		BodyPreference preview = SearchHandler.PreviewPreference(EasVersion.Parse(version));

		Assert.Equal(expected, preview.Eas16);
	}

	// A request whose offset is at/beyond the fetch cap must be refused without hitting the
	// backend (it would otherwise fetch the whole cap and Skip() it all away).
	[Fact]
	public async Task Search_PastFetchCap_SkipsBackend()
	{
		await InboxAsync();
		SeedHits(10);

		XDocument? response = await _harness.RunAsync(NewSearch(), "Search",
			new XDocument(new XElement(S + "Search",
				new XElement(S + "Store",
					new XElement(S + "Name", "Mailbox"),
					new XElement(S + "Query", new XElement(S + "FreeText", "hello")),
					new XElement(S + "Options", new XElement(S + "Range", "500-524"))))));

		Assert.Equal(0, _harness.Session.Mail.SearchCalls);
		XElement? store = response?.Root?.Element(S + "Response")?.Element(S + "Store");
		Assert.Equal("1", store?.Element(S + "Status")?.Value);
		Assert.Null(store?.Element(S + "Range"));
	}

	[Fact]
	public async Task Find_PastFetchCap_SkipsBackend()
	{
		await InboxAsync();
		SeedHits(10);

		XDocument? response = await _harness.RunAsync(NewFind(), "Find",
			new XDocument(new XElement(F + "Find",
				new XElement(F + "SearchId", "x"),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "MailBoxSearchCriterion",
						new XElement(F + "Query", new XElement(F + "FreeText", "hello")),
						new XElement(F + "Options", new XElement(F + "Range", "500-524")))))));

		Assert.Equal(0, _harness.Session.Mail.SearchCalls);
		XElement? resp = response?.Root?.Element(F + "Response");
		Assert.Equal("1", resp?.Element(F + "Status")?.Value);
		Assert.Null(resp?.Element(F + "Range"));
	}
}
