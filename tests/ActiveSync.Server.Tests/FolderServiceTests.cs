using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
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
		_service = new FolderService(new SyncStateService(_db), TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLogger<FolderService>.Instance);
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
			Id = 7, UserId = 1, BackendKey = "imap:INBOX", DisplayName = "Inbox", EasClass = EasClass.Email
		};
		// Correct prefix (or no prefix) → resolves to the raw mail UID.
		Assert.Equal("123", await _service.ResolveItemKeyAsync(folder, "7:123", CancellationToken.None));
		Assert.Equal("123", await _service.ResolveItemKeyAsync(folder, "123", CancellationToken.None));

		// Prefix names a different collection → refuse (would otherwise operate on UID 123
		// inside folder 7 regardless of what the client actually addressed).
		Assert.Null(await _service.ResolveItemKeyAsync(folder, "9:123", CancellationToken.None));
	}
}
