using ActiveSync.Backends.Local;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   A single undecryptable <see cref="LocalItem" /> row (e.g. written under a rotated
///   encryption key) must not fail the ENTIRE GAL search or the entire free/busy lookup —
///   AGENTS.md: "a free/busy failure must never fail the whole ResolveRecipients." Unlike
///   <see cref="LocalGalSearchTests" /> (plaintext protector, behaviour-preserving perf changes),
///   this uses a REAL encrypting protector so a row can be made genuinely undecryptable.
/// </summary>
public sealed class LocalStoreResilienceTests : IDisposable
{
	private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();

	private readonly SqliteConnection _connection;
	private readonly TestDbContextFactory _factory;
	private readonly LocalContentProtector _protector;
	private readonly int _userId;

	public LocalStoreResilienceTests()
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
		_protector = LocalContentProtector.CreateProtected(Key);
	}

	public void Dispose()
	{
		_protector.Dispose();
		_connection.Dispose();
	}

	[Fact]
	public async Task SearchGalAsync_SkipsAnUndecryptableRow_InsteadOfFailingTheWholeSearch()
	{
		SeedContact("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Alice Example\r\nEMAIL:alice@example.com\r\nEND:VCARD\r\n");
		SeedCorrupt("contacts");
		SeedContact("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:2\r\nFN:Alice Smith\r\nEMAIL:asmith@example.com\r\nEND:VCARD\r\n");

		LocalContactStore store = new(_factory, new LocalChangeNotifier(), _userId, _protector, NullLogger.Instance);

		IReadOnlyList<GalEntry> results = await store.SearchGalAsync("Alice", 25, null, CancellationToken.None);

		Assert.Equal(2, results.Count);
	}

	[Fact]
	public async Task GetBusyPeriodsAsync_SkipsAnUndecryptableRow_InsteadOfFailingTheWholeLookup()
	{
		SeedCalendar("""
		             BEGIN:VCALENDAR
		             VERSION:2.0
		             BEGIN:VEVENT
		             UID:evt-1
		             DTSTART:20260801T090000Z
		             DTEND:20260801T100000Z
		             SUMMARY:Meeting
		             END:VEVENT
		             END:VCALENDAR
		             """.ReplaceLineEndings("\r\n"));
		SeedCorrupt("calendar");

		LocalCalendarStore store = new(
			_factory, new LocalChangeNotifier(), _userId, _protector, "u@example.com", "u@example.com",
			NullLogger.Instance);

		IReadOnlyList<BusyPeriod>? busy = await store.GetBusyPeriodsAsync(
			"u@example.com", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
			new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

		Assert.NotNull(busy);
		Assert.Single(busy);
	}

	/// <summary>
	///   LocalCalendarStore's read sites (RespondToMeetingAsync, the ordinary payload fetch,
	///   GetEventAttachmentAsync) hard-coded the AAD collection literal instead of using
	///   <c>Collection</c>, duplicating the write side's own value. COVERAGE, not red-first proof:
	///   the literal and <c>Collection</c> agree today, so nothing observably breaks before the
	///   fix — the defect is fragility to a FUTURE typo/rename, not a current behaviour bug. This
	///   pins that the read path decrypts content sealed by the store's own write path
	///   (CreateItemAsync, which already used <c>Collection</c>) under a REAL encrypting protector.
	///   The dedicated raw-event read is gone with the typed currency: the ordinary fetch IS the
	///   raw read now, so it is what this asserts.
	/// </summary>
	[Fact]
	public async Task CalendarStore_ReadSites_DecryptContentSealedByTheStoresOwnWritePath()
	{
		LocalCalendarStore store = new(
			_factory, new LocalChangeNotifier(), _userId, _protector, "u@example.com", "u@example.com",
			NullLogger.Instance);

		FolderKey folder = new(store.FolderBackendKey);
		CalendarItem item = new()
		{
			ICalendar = """
			            BEGIN:VCALENDAR
			            VERSION:2.0
			            BEGIN:VEVENT
			            UID:evt-sealed
			            DTSTART:20260801T090000Z
			            DTEND:20260801T100000Z
			            SUMMARY:Planning
			            END:VEVENT
			            END:VCALENDAR
			            """.ReplaceLineEndings("\r\n")
		};
		(ItemKey itemKey, ItemRevision _) = await store.CreateItemAsync(folder, item, CancellationToken.None);

		CalendarItem? read = await store.GetItemAsync(folder, itemKey, CancellationToken.None);

		Assert.NotNull(read);
		Assert.Contains("BEGIN:VEVENT", read!.ICalendar);
		// Round-trip fidelity: what the host handed over is exactly what it gets back.
		Assert.Equal(item.ICalendar, read.ICalendar);
	}

	private void SeedContact(string vcf)
	{
		Seed("contacts", _protector.Protect(vcf, _userId, "contacts"));
	}

	private void SeedCalendar(string ics)
	{
		Seed("calendar", _protector.Protect(ics, _userId, "calendar"));
	}

	// A row that is well-formed AES-GCM but authenticated under a DIFFERENT AAD (protected as
	// "notes" instead of the collection it is stored under) — exactly what a rotated key or a
	// renamed bucket produces: Unprotect throws BackendException.
	private void SeedCorrupt(string collection)
	{
		Seed(collection, _protector.Protect("garbage", _userId, "notes"));
	}

	private void Seed(string collection, string content)
	{
		using SyncDbContext db = _factory.CreateDbContext();
		db.LocalItems.Add(new LocalItem
		{
			UserId = _userId,
			Collection = collection,
			Uid = Guid.NewGuid().ToString(),
			Content = content,
			Version = 1,
			LastModifiedUtc = DateTime.UtcNow
		});
		db.SaveChanges();
	}
}
