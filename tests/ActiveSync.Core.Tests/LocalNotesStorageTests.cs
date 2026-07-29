using ActiveSync.Backends.Local;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The notes STORAGE convention, exercised through the local notes store's public surface. The
///   shared notes converter is gone: notes are the one class whose contract payload is typed
///   (<see cref="NoteItem" />), the EAS XML half moved host-side, and VJOURNAL survives only as
///   this store's private at-rest format — which is precisely why it is asserted through the
///   store rather than against a converter. Kept from the old converter tests: subject/body/
///   categories round-trip, uid stability across an edit, and the unparsable-stored-row rules.
/// </summary>
public sealed class LocalNotesStorageTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly int _userId;

	public LocalNotesStorageTests()
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

	private LocalNotesStore Store() => new(
		new TestDbContextFactory(_connection), new LocalChangeNotifier(), _userId,
		LocalContentProtector.CreatePlaintext());

	private static NoteItem Note(string subject, string body, params string[] categories) => new()
	{
		Subject = subject,
		Body = new TextBody { Type = BodyType.PlainText, Content = body },
		Categories = categories
	};

	[Fact]
	public async Task RoundTrip_PreservesSubjectBodyAndCategories_AndStoresVJournal()
	{
		LocalNotesStore store = Store();
		FolderKey folder = new(store.FolderBackendKey);

		(ItemKey key, ItemRevision _) = await store.CreateItemAsync(
			folder, Note("Shopping list", "milk\nbread", "errands", "home"), CancellationToken.None);

		NoteItem? read = await store.GetItemAsync(folder, key, CancellationToken.None);
		Assert.NotNull(read);
		Assert.Equal("Shopping list", read!.Subject);
		Assert.Equal("milk\nbread", read.Body.Content);
		Assert.Equal(BodyType.PlainText, read.Body.Type);
		Assert.Equal(["errands", "home"], read.Categories);

		// VJOURNAL at rest is the whole reason the mapper stayed: existing sealed rows need no
		// migration.
		using SyncDbContext db = StateTestSupport.NewContext(_connection);
		LocalItem stored = await db.LocalItems.AsNoTracking().FirstAsync(i => i.Collection == "notes");
		Assert.Contains("VJOURNAL", stored.Content);
		Assert.False(string.IsNullOrEmpty(stored.Uid)); // the row's uid comes from the stored journal
	}

	[Fact]
	public async Task Update_PreservesTheUid_AndReplacesTheContent()
	{
		LocalNotesStore store = Store();
		FolderKey folder = new(store.FolderBackendKey);

		(ItemKey key, ItemRevision _) = await store.CreateItemAsync(
			folder, Note("v1", "first"), CancellationToken.None);
		string uidAfterCreate = await UidAsync();

		await store.UpdateItemAsync(folder, key, Note("v2", "second"), null, CancellationToken.None);

		Assert.Equal(uidAfterCreate, await UidAsync());
		NoteItem? read = await store.GetItemAsync(folder, key, CancellationToken.None);
		Assert.Equal("v2", read!.Subject);
		Assert.Equal("second", read.Body.Content);
	}

	[Fact]
	public async Task UnparsableStoredNote_ReadsAsNotFetched_RatherThanThrowing()
	{
		// A corrupt row costs one note, never the whole Sync batch (null = "not fetched").
		ItemKey key = new((await SeedRawAsync("this is not an ical at all")).ToString());
		LocalNotesStore store = Store();

		Assert.Null(await store.GetItemAsync(new FolderKey(store.FolderBackendKey), key, CancellationToken.None));
	}

	[Fact]
	public async Task UpdatingAnUnparsableStoredNote_SurfacesABackendException()
	{
		// The merge path loads the stored journal to preserve unmapped properties; Ical.Net throws
		// on genuinely unparsable text, and the store must degrade to a BackendException rather
		// than leak the serializer's own type.
		ItemKey key = new((await SeedRawAsync("this is not an ical at all")).ToString());
		LocalNotesStore store = Store();

		await Assert.ThrowsAsync<BackendException>(() => store.UpdateItemAsync(
			new FolderKey(store.FolderBackendKey), key, Note("Renamed", "body"), null, CancellationToken.None));
	}

	private async Task<int> SeedRawAsync(string content)
	{
		await using SyncDbContext db = StateTestSupport.NewContext(_connection);
		LocalItem row = new()
		{
			UserId = _userId,
			Collection = "notes",
			Uid = "n-1",
			Content = content,
			Version = 1,
			LastModifiedUtc = DateTime.UtcNow
		};
#pragma warning disable VSTHRD103
		db.LocalItems.Add(row);
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();
		return row.Id;
	}

	private async Task<string> UidAsync()
	{
		await using SyncDbContext db = StateTestSupport.NewContext(_connection);
		return (await db.LocalItems.AsNoTracking().FirstAsync(i => i.Collection == "notes")).Uid;
	}
}
