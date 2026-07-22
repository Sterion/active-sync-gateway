using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Server.Eas;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

public sealed class FolderServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SqliteSyncDbContext _db;
	private readonly FolderService _service;

	public FolderServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection).Options;
		_db = new SqliteSyncDbContext(options);
		_db.Database.EnsureCreated();
		_service = new FolderService(new SyncStateService(_db), NullLogger<FolderService>.Instance);
	}

	public void Dispose()
	{
		_db.Dispose();
		_connection.Dispose();
	}

	[Fact]
	public async Task ResolveItemKey_RejectsMismatchedCollectionPrefix()
	{
		UserFolder folder = new()
		{
			Id = 7, UserName = "u@x", BackendKey = "imap:INBOX", DisplayName = "Inbox", EasClass = EasClass.Email
		};
		MailStoreStub store = new();

		// Correct prefix (or no prefix) → resolves to the raw mail UID.
		Assert.Equal("123", await _service.ResolveItemKeyAsync(folder, store, "7:123", CancellationToken.None));
		Assert.Equal("123", await _service.ResolveItemKeyAsync(folder, store, "123", CancellationToken.None));

		// Prefix names a different collection → refuse (would otherwise operate on UID 123
		// inside folder 7 regardless of what the client actually addressed).
		Assert.Null(await _service.ResolveItemKeyAsync(folder, store, "9:123", CancellationToken.None));
	}

	private sealed class MailStoreStub : IContentStore
	{
		public string EasClass => Protocol.EasClass.Email;

		public bool OwnsBackendKey(string backendKey) =>
			backendKey.StartsWith("imap:", StringComparison.Ordinal);

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
			string folderBackendKey, ContentFilter filter, CancellationToken ct) => throw new NotSupportedException();

		public Task<BackendItem?> GetItemAsync(
			string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(string ItemKey, string Revision)> CreateItemAsync(
			string folderBackendKey, XElement applicationData, CancellationToken ct) => throw new NotSupportedException();

		public Task<string> UpdateItemAsync(
			string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteItemAsync(string folderBackendKey, string itemKey, bool permanent, CancellationToken ct) =>
			throw new NotSupportedException();

		// K58: item move and folder mutation are optional capabilities; this stub implements neither.

		public Task<IReadOnlyList<string>> WaitForChangesAsync(
			IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
