using System.Reflection;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Session-lifetime behaviour of <see cref="BackendSessionFactory" />: the refcounted lease that
///   keeps an in-use session alive across an idle-eviction sweep (A2), that the share-grant DB read
///   happens once per build rather than once per request (A11), and that a disposed factory
///   unsubscribes from snapshot/settings events (A28).
/// </summary>
public sealed class BackendSessionFactoryTests : IDisposable
{
	private static readonly BackendCredentials Creds = new("user1@example.com", "pass");

	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _dbFactory;

	public BackendSessionFactoryTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbFactory = new TestContextFactory(_connection);
		using SyncDbContext db = _dbFactory.CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task IdleEviction_DoesNotTearDownASessionAnActiveRequestHolds()
	{
		// A2: a Ping holds its session for the whole heartbeat (up to ~29.5 min), far longer than
		// SessionIdleMinutes, so the idle sweep fires while the request is mid-flight. It must evict
		// the session from the cache WITHOUT disposing the connection the request is still using.
		FakeMailProvider provider = new();
		BackendSessionFactory factory = NewFactory(provider, sessionIdleMinutes: -1); // everything is "idle"

		// The active request gets its session and keeps using it.
		IBackendSession held = await factory.GetSessionAsync(Creds, "dev-1", CancellationToken.None);
		FakeResource resource = provider.LastResource!;

		// The idle sweep runs concurrently with the still-open request.
		InvokeEvictIdleSessions(factory);

		Assert.Empty(factory.SnapshotSessions()); // the sweep DID evict it from the cache
		// ...but the connection the request holds must survive until the request releases it.
		await Task.Delay(300);
		Assert.False(resource.Disposed);

		// Releasing the request's lease is what finally tears the connection down.
		await held.DisposeAsync();
		Assert.True(await WaitUntil(() => resource.Disposed, TimeSpan.FromSeconds(2)));
	}

	private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
				return true;
			await Task.Delay(20);
		}

		return condition();
	}

	[Fact]
	public async Task ShareGrants_AreReadOncePerBuild_NotPerRequest()
	{
		// A11: LoadShareGrantsAsync opened a DbContext on every GetSessionAsync, though the grants
		// are consumed only when a session is actually built. A cache hit must not touch the DB.
		FakeMailProvider provider = new();
		CountingContextFactory counting = new(_connection);
		BackendSessionFactory factory = NewFactory(provider, dbFactory: counting);

		await factory.GetSessionAsync(Creds, "dev-1", CancellationToken.None);
		int afterBuild = counting.Created;
		await factory.GetSessionAsync(Creds, "dev-1", CancellationToken.None); // cache hit
		await factory.GetSessionAsync(Creds, "dev-1", CancellationToken.None); // cache hit

		Assert.Equal(afterBuild, counting.Created); // no further DB reads for the cache hits
	}

	[Fact]
	public async Task DisposedFactory_UnsubscribesFromSettingsEvents()
	{
		// A28: the factory subscribed to BackendRolesProvider.Changed / AccountResolver.SnapshotChanged
		// but never unsubscribed. After disposal it must detach both handlers, otherwise the disposed
		// (dead) factory stays reachable and its handlers keep firing on cleared state.
		FakeMailProvider provider = new();
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(new ActiveSyncOptions
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true }, Eas = new EasOptions()
		});
		BackendRolesProvider roles = RolesProvider();
		BackendProviderRegistry registry = new([provider], NullLogger<BackendProviderRegistry>.Instance);
		AccountResolver resolver = new(monitor, roles, registry);
		BackendSessionFactory factory = new(monitor, resolver, roles, _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);

		// Both events carry a handler that targets the factory while it is alive (the resolver also
		// subscribes to roles.Changed for itself — so we check specifically for the factory's handler).
		Assert.True(HasHandlerTargeting(roles, "Changed", factory));
		Assert.True(HasHandlerTargeting(resolver, "SnapshotChanged", factory));

		await factory.DisposeAsync();

		Assert.False(HasHandlerTargeting(roles, "Changed", factory));
		Assert.False(HasHandlerTargeting(resolver, "SnapshotChanged", factory));
	}

	[Fact]
	public void IdleSweep_SwallowsAnEscapingException()
	{
		// A13: EvictIdleSessions is a System.Threading.Timer callback — an escaping exception
		// terminates the process. Reading _options.CurrentValue can throw (live-editable settings),
		// so the whole body must be guarded.
		FakeMailProvider provider = new();
		ToggleThrowMonitor monitor = new(new ActiveSyncOptions
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true }, Eas = new EasOptions()
		});
		BackendRolesProvider roles = RolesProvider();
		BackendProviderRegistry registry = new([provider], NullLogger<BackendProviderRegistry>.Instance);
		AccountResolver resolver = new(monitor, roles, registry);
		BackendSessionFactory factory = new(monitor, resolver, roles, _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);

		monitor.Throw = true; // the sweep will now hit a throwing options read
		Exception? escaped = Record.Exception(() => InvokeEvictIdleSessions(factory));
		Assert.Null(escaped); // the timer callback must not let it escape
	}

	// ---------- harness ----------

	private sealed class ToggleThrowMonitor(ActiveSyncOptions value) : IOptionsMonitor<ActiveSyncOptions>
	{
		public bool Throw { get; set; }
		public ActiveSyncOptions CurrentValue => Throw ? throw new InvalidOperationException("options invalid") : value;
		public ActiveSyncOptions Get(string? name) => CurrentValue;
		public IDisposable? OnChange(Action<ActiveSyncOptions, string?> listener) => null;
	}

	private static bool HasHandlerTargeting(object eventSource, string eventName, object handlerTarget)
	{
		Delegate? backing = (Delegate?)eventSource.GetType()
			.GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(eventSource);
		return backing?.GetInvocationList().Any(d => ReferenceEquals(d.Target, handlerTarget)) ?? false;
	}

	private static void InvokeEvictIdleSessions(BackendSessionFactory factory) =>
		typeof(BackendSessionFactory)
			.GetMethod("EvictIdleSessions", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(factory, null);

	private BackendSessionFactory NewFactory(
		FakeMailProvider provider,
		int sessionIdleMinutes = 15,
		ISyncDbContextFactory? dbFactory = null,
		BackendRolesProvider? roles = null)
	{
		ActiveSyncOptions options = new()
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true },
			Eas = new EasOptions { SessionIdleMinutes = sessionIdleMinutes }
		};
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(options);
		BackendRolesProvider rolesProvider = roles ?? RolesProvider();
		BackendProviderRegistry registry =
			new([provider], NullLogger<BackendProviderRegistry>.Instance);
		AccountResolver resolver = new(monitor, rolesProvider, registry);
		return new BackendSessionFactory(monitor, resolver, rolesProvider, dbFactory ?? _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);
	}

	private static BackendRolesProvider RolesProvider()
	{
		Dictionary<string, string?> config = new();
		foreach (string role in new[] { "MailStore", "MailSubmit", "Calendar", "Contacts", "Tasks", "Notes" })
			config[$"ActiveSync:Backends:{role}:Provider"] = "fake";
		IConfigurationRoot root = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
		return new BackendRolesProvider(root);
	}

	private sealed class TestContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext() =>
			new SqliteSyncDbContext(new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection).Options);
	}

	private sealed class CountingContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public int Created { get; private set; }

		public SyncDbContext CreateDbContext()
		{
			Created++;
			return new SqliteSyncDbContext(new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection).Options);
		}
	}

	private sealed class FakeMailProvider : IBackendProvider
	{
		private static readonly IReadOnlySet<BackendRole> All = new HashSet<BackendRole>
		{
			BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar,
			BackendRole.Contacts, BackendRole.Tasks, BackendRole.Notes
		};

		public FakeResource? LastResource { get; private set; }

		public string Name => "fake";
		public IReadOnlySet<BackendRole> SupportedRoles => All;

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "fake";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
		{
			LastResource = new FakeResource();
			return Task.FromResult<IBackendConnection>(
				new BackendConnection([new FakeMailStore()], new FakeSubmit(), ownedResources: [LastResource]));
		}
	}

	private sealed class FakeResource : IAsyncDisposable
	{
		public bool Disposed { get; private set; }

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeSubmit : IMailSubmitOperations
	{
		public Task SendAsync(byte[] mime, CancellationToken ct) => Task.CompletedTask;
	}

	private sealed class FakeMailStore : IContentStore, IMailStoreOperations
	{
		public string EasClass => "Email";
		public bool OwnsBackendKey(string backendKey) => backendKey.StartsWith("fake:", StringComparison.Ordinal);

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

		public Task<IReadOnlyList<string>> WaitForChangesAsync(
			IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SaveToSentAsync(byte[] mime, CancellationToken ct) => throw new NotSupportedException();

		public Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SetAnsweredAsync(string folderBackendKey, string itemKey, bool forwarded, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
			string? folderBackendKey, string freeText, DateTime? sinceUtc, int maxResults, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct) => throw new NotSupportedException();
	}
}
