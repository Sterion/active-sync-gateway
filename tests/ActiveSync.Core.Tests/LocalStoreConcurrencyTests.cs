using ActiveSync.Backends.Local;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Local writes race concurrent devices through <see cref="LocalItem.ConcurrencyToken" />
///   (stamped on every save — <see cref="SyncDbContext" />). <see cref="LocalStoreBase.UpdateItemAsync" />
///   already retries a losing attempt up to 4 times; these tests cover the two paths that did not
///   behave like every other local write: an update that EXHAUSTS its retries must surface a
///   <see cref="BackendException" />, not a raw EF type, and
///   <see cref="LocalCalendarStore.RespondToMeetingAsync" /> must retry a losing attempt at all.
/// </summary>
public sealed class LocalStoreConcurrencyTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly int _userId;
	private readonly string _uid = Guid.NewGuid().ToString();

	public LocalStoreConcurrencyTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		db.Database.EnsureCreated();
		User user = new() { Login = "u", UpdatedUtc = DateTime.UtcNow };
#pragma warning disable VSTHRD103
		db.Users.Add(user);
#pragma warning restore VSTHRD103
		db.SaveChanges();
		_userId = user.UserId;
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task UpdateItemAsync_ExhaustingRetries_ThrowsBackendException_NotTheRawEfType()
	{
		SeedContact();

		// Every single attempt races a fresh competing write on the SAME row, so all 4 attempts
		// collide and the retry loop is genuinely exhausted (not just raced once and recovered).
		AlwaysConflictInterceptor race = new(() =>
		{
			using SyncDbContext racer = StateTestSupport.NewContext(_connection);
			LocalItem row = racer.LocalItems.First(i => i.Uid == _uid);
			row.LastModifiedUtc = DateTime.UtcNow;
			racer.SaveChanges();
		});
		InterceptedDbContextFactory factory = new(_connection, race);
		LocalContactStore store = new(
			factory, new LocalChangeNotifier(), _userId, LocalContentProtector.CreatePlaintext(), NullLogger.Instance);

		ItemKey itemKey = await ItemKeyAsync();
		ContactItem updated = new()
		{
			VCard = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Updated\r\nEND:VCARD\r\n"
		};

		await Assert.ThrowsAsync<BackendException>(() =>
			store.UpdateItemAsync(new FolderKey(store.FolderBackendKey), itemKey, updated, null, CancellationToken.None));
	}

	private void SeedContact()
	{
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		db.LocalItems.Add(new LocalItem
		{
			UserId = _userId,
			Collection = "contacts",
			Uid = _uid,
			Content = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:1\r\nFN:Original\r\nEND:VCARD\r\n",
			Version = 1,
			LastModifiedUtc = DateTime.UtcNow
		});
		db.SaveChanges();
	}

	private async Task<ItemKey> ItemKeyAsync()
	{
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		LocalItem row = await db.LocalItems.FirstAsync(i => i.Uid == _uid);
		return new ItemKey(row.Id.ToString());
	}

	[Fact]
	public async Task RespondToMeetingAsync_RetriesOnceOnAConcurrentWrite_LikeEverySiblingWrite()
	{
		string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:meet-1
		                    DTSTART:20260801T090000Z
		                    DTEND:20260801T100000Z
		                    SUMMARY:Planning
		                    ORGANIZER:mailto:boss@example.com
		                    ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:me@example.com
		                    END:VEVENT
		                    END:VCALENDAR
		                    """.ReplaceLineEndings("\r\n");
		SeedEvent(ics);

		// A SINGLE competing write races the first attempt's save — a real device bumping the
		// same row between our read and save — then goes quiet, so a bounded retry (matching
		// LocalStoreBase.UpdateItemAsync's own 4-attempt loop) must recover on the very next
		// attempt instead of losing the response.
		ConcurrentWriteInterceptor race = new(() =>
		{
			using SyncDbContext racer = StateTestSupport.NewContext(_connection);
			LocalItem row = racer.LocalItems.First(i => i.Uid == "meet-1");
			row.LastModifiedUtc = DateTime.UtcNow;
			racer.SaveChanges();
		});
		InterceptedDbContextFactory factory = new(_connection, race);
		LocalCalendarStore store = new(
			factory, new LocalChangeNotifier(), _userId, LocalContentProtector.CreatePlaintext(),
			"me@example.com", "me@example.com", NullLogger.Instance);

		ItemKey? id = await store.RespondToMeetingAsync(
			new FolderKey(store.FolderBackendKey), "meet-1", MeetingResponseKind.Accepted, CancellationToken.None);

		Assert.NotNull(id); // must not throw DbUpdateConcurrencyException — the response must land

		using SyncDbContext verify = StateTestSupport.NewContext(_connection);
		LocalItem stored = await verify.LocalItems.AsNoTracking().FirstAsync(i => i.Uid == "meet-1");
		Assert.Contains("PARTSTAT=ACCEPTED", stored.Content);
	}

	private void SeedEvent(string ics)
	{
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		db.LocalItems.Add(new LocalItem
		{
			UserId = _userId,
			Collection = "calendar",
			Uid = "meet-1",
			Content = ics,
			Version = 1,
			LastModifiedUtc = DateTime.UtcNow
		});
		db.SaveChanges();
	}
}
