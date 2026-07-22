using System.Xml.Linq;
using ActiveSync.Backends.Local;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D19: GAL search over the local contact store used to ToListAsync the WHOLE collection, then
///   decrypt and vCard-parse each matching card three times. The fix streams the rows
///   (AsAsyncEnumerable so the maxResults break stops work), parses each card once (BuildGalEntry)
///   and reads with AsNoTracking. These are behaviour-preserving performance changes, so this is
///   COVERAGE, not a red-first reproducer — it pins that GAL search still returns the right
///   entries and photos through the rewritten path.
/// </summary>
public sealed class LocalGalSearchTests : IDisposable
{
	private static readonly XNamespace Gal = EasNamespaces.Gal;
	private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

	private readonly SqliteConnection _connection;
	private readonly TestDbContextFactory _factory;
	private readonly LocalContactStore _store;

	public LocalGalSearchTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestDbContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new LocalContactStore(
			_factory, new LocalChangeNotifier(), new BackendCredentials("u", "p"),
			LocalContentProtector.CreatePlaintext());
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task SearchGal_MatchesByName_AcrossTheCollection()
	{
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Alice Example\r\nEMAIL:alice@example.com\r\nEND:VCARD\r\n");
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:2\r\nFN:Bob Example\r\nEMAIL:bob@example.com\r\nEND:VCARD\r\n");
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:3\r\nFN:Alice Smith\r\nEMAIL:asmith@example.com\r\nEND:VCARD\r\n");

		IReadOnlyList<IReadOnlyList<XElement>> results =
			await _store.SearchGalAsync("Alice", 25, null, CancellationToken.None);

		List<string> names = results
			.Select(e => e.First(x => x.Name == Gal + "DisplayName").Value)
			.OrderBy(n => n).ToList();
		Assert.Equal(["Alice Example", "Alice Smith"], names);
	}

	[Fact]
	public async Task SearchGal_WithPhotoRequest_ReturnsTheDecodedPhoto()
	{
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Photo Person\r\n" +
		     $"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(PhotoBytes)}\r\nEND:VCARD\r\n");

		IReadOnlyList<IReadOnlyList<XElement>> results =
			await _store.SearchGalAsync("Photo", 25, new GalPhotoRequest(null, null), CancellationToken.None);

		IReadOnlyList<XElement> entry = Assert.Single(results);
		XElement picture = entry.First(x => x.Name == Gal + "Picture");
		Assert.Equal("1", picture.Element(Gal + "Status")?.Value);
		Assert.Equal(PhotoBytes, Convert.FromBase64String(picture.Element(Gal + "Data")!.Value));
	}

	private void Seed(string vcf)
	{
		using SyncDbContext db = _factory.CreateDbContext();
		db.LocalItems.Add(new LocalItem
		{
			UserName = "u",
			Collection = "contacts",
			Uid = Guid.NewGuid().ToString(),
			Content = vcf,
			Version = 1,
			LastModifiedUtc = DateTime.UtcNow
		});
		db.SaveChanges();
	}
}
