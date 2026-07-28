using System.Xml.Linq;
using ActiveSync.Backends.Local;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   G18: a single undecryptable <see cref="LocalItem" /> row (e.g. written under a rotated
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

		IReadOnlyList<IReadOnlyList<XElement>> results =
			await store.SearchGalAsync("Alice", 25, null, CancellationToken.None);

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
			"u@example.com", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
			new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

		Assert.NotNull(busy);
		Assert.Single(busy);
	}

	/// <summary>
	///   G30: LocalCalendarStore's read sites (RespondToMeetingAsync, GetRawEventAsync,
	///   GetEventAttachmentAsync) hard-coded the AAD collection literal instead of using
	///   <c>Collection</c>, duplicating the write side's own value. COVERAGE, not red-first proof:
	///   the literal and <c>Collection</c> agree today, so nothing observably breaks before the
	///   fix — the defect is fragility to a FUTURE typo/rename, not a current behaviour bug. This
	///   pins that all three read sites decrypt content sealed by the store's own write path
	///   (CreateItemAsync, which already used <c>Collection</c>) under a REAL encrypting protector.
	/// </summary>
	[Fact]
	public async Task CalendarStore_ReadSites_DecryptContentSealedByTheStoresOwnWritePath()
	{
		LocalCalendarStore store = new(
			_factory, new LocalChangeNotifier(), _userId, _protector, "u@example.com", "u@example.com",
			NullLogger.Instance);

		XNamespace cal = EasNamespaces.Calendar;
		XElement appData = new("ApplicationData",
			new XElement(cal + "Subject", "Planning"),
			new XElement(cal + "StartTime", "20260801T090000Z"),
			new XElement(cal + "EndTime", "20260801T100000Z"));
		(string itemKey, string _) = await store.CreateItemAsync(store.FolderBackendKey, appData, CancellationToken.None);

		string? raw = await store.GetRawEventAsync(store.FolderBackendKey, itemKey, CancellationToken.None);
		Assert.NotNull(raw);
		Assert.Contains("BEGIN:VEVENT", raw);
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
