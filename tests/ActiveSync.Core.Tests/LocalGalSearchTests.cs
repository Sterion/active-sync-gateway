using ActiveSync.Backends.Local;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   GAL search over the local contact store used to ToListAsync the WHOLE collection, then
///   decrypt and vCard-parse each matching card three times. The fix streams the rows
///   (AsAsyncEnumerable so the maxResults break stops work), parses each card once (BuildGalEntry)
///   and reads with AsNoTracking. These are behaviour-preserving performance changes, so this is
///   COVERAGE, not a red-first reproducer — it pins that GAL search still returns the right
///   entries and photos through the rewritten path.
/// </summary>
public sealed class LocalGalSearchTests : IDisposable
{
	private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

	private readonly SqliteConnection _connection;
	private readonly TestDbContextFactory _factory;
	private readonly LocalContactStore _store;

	private readonly int _userId;

	public LocalGalSearchTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestDbContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		User user = new() { Login = "u", UpdatedUtc = DateTime.UtcNow };
#pragma warning disable VSTHRD103
		db.Users.Add(user);
#pragma warning restore VSTHRD103
		db.SaveChanges();
		_userId = user.UserId;
		_store = new LocalContactStore(
			_factory, new LocalChangeNotifier(), _userId,
			LocalContentProtector.CreatePlaintext(), NullLogger.Instance);
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task SearchGal_MatchesByName_AcrossTheCollection()
	{
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Alice Example\r\nEMAIL:alice@example.com\r\nEND:VCARD\r\n");
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:2\r\nFN:Bob Example\r\nEMAIL:bob@example.com\r\nEND:VCARD\r\n");
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:3\r\nFN:Alice Smith\r\nEMAIL:asmith@example.com\r\nEND:VCARD\r\n");

		IReadOnlyList<GalEntry> results = await _store.SearchGalAsync("Alice", 25, null, CancellationToken.None);

		List<string> names = results.Select(e => e.DisplayName).OrderBy(n => n).ToList();
		Assert.Equal(["Alice Example", "Alice Smith"], names);
	}

	/// <summary>
	///   <c>SearchGalAsync</c>'s <c>maxResults</c> break was checked BEFORE adding the current
	///   match, one row later than the streaming rewrite's stated intent ("stops pulling and decrypting
	///   rows once enough matches are found") — the enumerator had already pulled/materialized one
	///   extra row by the time the old check ran. The fix moves the check to after the add. COVERAGE,
	///   not red-first proof: the actual waste is one extra streamed-row fetch from the open SQLite
	///   reader, which isn't observable through this store's public surface (both the old and the new
	///   code decrypt/return exactly <c>maxResults</c> matches — Unprotect is never called on the
	///   extra row either way) without instrumenting EF's DbDataReader, so this pins the
	///   still-correct limiting behaviour rather than reproducing the fetch itself.
	/// </summary>
	[Fact]
	public async Task SearchGal_StopsAtMaxResults_WhenMoreRowsMatch()
	{
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Alice One\r\nEMAIL:one@example.com\r\nEND:VCARD\r\n");
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:2\r\nFN:Alice Two\r\nEMAIL:two@example.com\r\nEND:VCARD\r\n");
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:3\r\nFN:Alice Three\r\nEMAIL:three@example.com\r\nEND:VCARD\r\n");

		IReadOnlyList<GalEntry> results = await _store.SearchGalAsync("Alice", 2, null, CancellationToken.None);

		Assert.Equal(2, results.Count);
	}

	[Fact]
	public async Task SearchGal_WithPhotoRequest_ReturnsTheDecodedPhoto()
	{
		Seed("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Photo Person\r\n" +
		     $"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(PhotoBytes)}\r\nEND:VCARD\r\n");

		IReadOnlyList<GalEntry> results = await _store.SearchGalAsync(
			"Photo", 25, new GalPhotoRequest { MaxSizeBytes = null, MaxCount = null }, CancellationToken.None);

		GalEntry entry = Assert.Single(results);
		Assert.Equal(GalPictureStatus.Available, entry.Picture!.Status);
		Assert.Equal(PhotoBytes, entry.Picture.Picture!.Data.ToArray());
	}

	private void Seed(string vcf)
	{
		using SyncDbContext db = _factory.CreateDbContext();
		db.LocalItems.Add(new LocalItem
		{
			UserId = _userId,
			Collection = "contacts",
			Uid = Guid.NewGuid().ToString(),
			Content = vcf,
			Version = 1,
			LastModifiedUtc = DateTime.UtcNow
		});
		db.SaveChanges();
	}
}
