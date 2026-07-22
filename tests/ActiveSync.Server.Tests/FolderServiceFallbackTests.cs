using System.Data.Common;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Server.Eas;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>E25 — the folder-listing fallback must not re-query the registry per failing store.</summary>
public sealed class FolderServiceFallbackTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly UserFolderQueryCounter _counter = new();

	public FolderServiceFallbackTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
	}

	public void Dispose() => _connection.Dispose();

	private FolderService NewService()
	{
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection)
			.AddInterceptors(_counter)
			.Options;
		SqliteSyncDbContext db = new(options);
		db.Database.EnsureCreated();
		return new FolderService(new SyncStateService(db), NullLogger<FolderService>.Instance);
	}

	// When several DAV stores are down at once (the common correlated case), the catch used to
	// call state.GetFoldersAsync — a full registry read — once PER failing store. The number of
	// registry reads must not scale with the count of failing stores.
	[Fact]
	public async Task FolderListingFallback_DoesNotRequeryRegistryPerFailingStore()
	{
		int oneFailingStore = await CountRegistryReadsAsync(1);
		int threeFailingStores = await CountRegistryReadsAsync(3);

		// RefreshFolderRegistry issues a constant number of registry reads regardless of the
		// store count (all stores fail, the registry is empty); the only thing that scaled with
		// the store count was the redundant per-store fallback read this finding removes.
		Assert.Equal(oneFailingStore, threeFailingStores);
	}

	private async Task<int> CountRegistryReadsAsync(int failingStores)
	{
		FolderService service = NewService();
		IContentStore[] stores = Enumerable.Range(0, failingStores)
			.Select(i => (IContentStore)new FailingStore($"caldav-{i}:"))
			.ToArray();
		_counter.Reset();
		await service.RefreshAsync(new StoresOnlySession(stores), "u@example.test", CancellationToken.None);
		return _counter.RegistryReads;
	}

	/// <summary>Counts SELECTs against the UserFolders table.</summary>
	private sealed class UserFolderQueryCounter : DbCommandInterceptor
	{
		public int RegistryReads { get; private set; }

		public void Reset() => RegistryReads = 0;

		public override InterceptionResult<DbDataReader> ReaderExecuting(
			DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
		{
			Count(command.CommandText);
			return base.ReaderExecuting(command, eventData, result);
		}

		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
			DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
			CancellationToken cancellationToken = default)
		{
			Count(command.CommandText);
			return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
		}

		private void Count(string sql)
		{
			if (sql.Contains("\"UserFolders\"", StringComparison.Ordinal))
				RegistryReads++;
		}
	}

	/// <summary>A content store whose folder listing always fails.</summary>
	private sealed class FailingStore(string keyPrefix) : IContentStore
	{
		public string EasClass => keyPrefix;

		public bool OwnsBackendKey(string backendKey) =>
			backendKey.StartsWith(keyPrefix, StringComparison.Ordinal);

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new InvalidOperationException("backend down");

		public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
			string folderBackendKey, ContentFilter filter, CancellationToken ct) => throw new NotSupportedException();

		public Task<BackendItem?> GetItemAsync(
			string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(string ItemKey, string Revision)> CreateItemAsync(
			string folderBackendKey, System.Xml.Linq.XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<string> UpdateItemAsync(
			string folderBackendKey, string itemKey, System.Xml.Linq.XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteItemAsync(string folderBackendKey, string itemKey, bool permanent, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<string>> WaitForChangesAsync(
			IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	/// <summary>A session that exposes only its store list — enough for FolderService.RefreshAsync.</summary>
	private sealed class StoresOnlySession(IReadOnlyList<IContentStore> stores) : IBackendSession
	{
		public BackendCredentials Credentials => new("u@example.test", "pw");
		public string? MailAddress => "u@example.test";
		public IReadOnlyList<IContentStore> Stores => stores;
		public IMailStoreOperations MailStore => null!;
		public IMailSubmitOperations MailSubmit => null!;
		public IContactOperations? Contacts => null;
		public ICalendarOperations? Calendar => null;
		public IOofBackend? Oof => null;
		public IContentStore? GetStoreForClass(string easClass) => null;
		public IContentStore? GetStoreForBackendKey(string backendKey) => null;
		public bool IsReadOnlyFolder(string folderBackendKey) => false;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
