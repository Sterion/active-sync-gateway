using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Search (2.2.1.16) and Find (2.2.1.2) paging/shape conformance: Total reports the number of
///   matches, not the served page size (F36); Find omits Range when empty (F37) and orders its
///   Result children per spec (F38); paging past the fetch cap is refused without a backend call
///   (F41).
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
		// An encodable body so a Search/Find hit round-trips through the WBXML codec.
		_harness.Session.Store.ItemApplicationData = _ =>
			[new XElement(ASB + "Body", new XElement(ASB + "Type", "1"), new XElement(ASB + "Data", "preview"))];
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
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return registry.Single();
	}

	private void SeedHits(int count)
	{
		for (int i = 1; i <= count; i++)
			_harness.Session.Mail.SearchHits.Add(("imap:INBOX", i.ToString()));
	}

	// F36 — Search Total must report the number of matches found, not the served page size. With
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

	// F36 — Find Total must not overreport past the end of the hit set. With 3 hits, a request for
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

	// F37 — Find must omit Range when there are no results; "0-0" claims one result was returned.
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

	// F38 — Find Result child order must be Class, ServerId, CollectionId, Properties (MS-ASCMD),
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

	// F41 — a request whose offset is at/beyond the fetch cap must be refused without hitting the
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
