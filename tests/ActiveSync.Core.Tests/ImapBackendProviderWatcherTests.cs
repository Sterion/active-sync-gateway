using System.Collections.Concurrent;
using System.Reflection;
using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

/// <summary>
///   G7: the shared per-(user, folder) IDLE watcher is rebuilt only when the PASSWORD changes, so
///   a per-user backend edit that changes host/port/security leaves a live watcher connected to
///   the decommissioned server. <see cref="ImapIdleWatcher" />'s constructor does no I/O (it only
///   connects lazily once a wait is registered), so this is directly unit-testable: build two
///   watchers for the same (user, folder) with the same password but different resolved
///   <see cref="ImapOptions" />, and the provider must NOT hand back the same cached instance.
///   <para>
///     G22 adds the second per-user resource the provider owns — the shared STATUS-poll
///     connection (<see cref="ImapStatusPoller" />) — which is cached, rebuilt and evicted by the
///     same rules, and is likewise constructible without I/O.
///   </para>
/// </summary>
public sealed class ImapBackendProviderWatcherTests : IAsyncLifetime
{
	private readonly ImapBackendProvider _provider;

	public ImapBackendProviderWatcherTests()
	{
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(new ActiveSyncOptions());
		_provider = new ImapBackendProvider(monitor, NullLoggerFactory.Instance);
	}

	public Task InitializeAsync() => Task.CompletedTask;
	public async Task DisposeAsync() => await _provider.DisposeAsync();

	[Fact]
	public void GetOrCreateWatcher_SamePasswordDifferentHost_ReturnsADifferentWatcher()
	{
		BackendCredentials credentials = new("bob@example.com", "same-password");
		ImapOptions original = new() { Host = "old-imap.example.com", Port = 143 };
		ImapOptions moved = new() { Host = "new-imap.example.com", Port = 143 };

		ImapIdleWatcher? first = _provider.GetOrCreateWatcher("bob@example.com", original, credentials, "INBOX");
		ImapIdleWatcher? second = _provider.GetOrCreateWatcher("bob@example.com", moved, credentials, "INBOX");

		Assert.NotNull(first);
		Assert.NotNull(second);
		// The password never changed, so today's key comparison hands back the SAME watcher --
		// which now points at "old-imap.example.com" while every backend session moved to
		// "new-imap.example.com". They must be different instances once the resolved options are
		// part of the cache key.
		Assert.NotSame(first, second);
	}

	[Fact]
	public void GetOrCreateWatcher_SameEverything_ReturnsTheSameWatcher()
	{
		BackendCredentials credentials = new("bob@example.com", "same-password");
		ImapOptions options = new() { Host = "imap.example.com", Port = 143 };

		ImapIdleWatcher? first = _provider.GetOrCreateWatcher("bob@example.com", options, credentials, "INBOX");
		ImapIdleWatcher? second = _provider.GetOrCreateWatcher("bob@example.com", options, credentials, "INBOX");

		Assert.Same(first, second);
	}

	/// <summary>
	///   G22 (coverage, not proof — the symptom itself is proven red-first against a real IMAP
	///   server by <c>WaitForChangesAsync_PollsOverOneOwnConnection_NotTheSessionGate</c>, which
	///   counts connections; this pins the CACHE half of the same invariant deterministically, in
	///   the suite that always runs). The STATUS-poll connection is one per gateway user, shared by
	///   every device and folder — so the provider must hand back the SAME poller, and it must
	///   survive across sessions rather than being rebuilt per connection.
	/// </summary>
	[Fact]
	public void GetOrCreatePoller_SameEverything_ReturnsTheSamePoller()
	{
		BackendCredentials credentials = new("bob@example.com", "same-password");
		ImapOptions options = new() { Host = "imap.example.com", Port = 143 };

		ImapStatusPoller first = _provider.GetOrCreatePoller("bob@example.com", options, credentials);
		ImapStatusPoller second = _provider.GetOrCreatePoller("bob@example.com", options, credentials);

		Assert.Same(first, second);
	}

	/// <summary>
	///   G22 + G7: the poll connection carries the same rebuild rule as the IDLE watcher — a
	///   per-user host/port/security edit must not leave an authenticated poll connection open
	///   against the decommissioned server.
	/// </summary>
	[Fact]
	public void GetOrCreatePoller_SamePasswordDifferentHost_ReturnsADifferentPoller()
	{
		BackendCredentials credentials = new("bob@example.com", "same-password");
		ImapOptions original = new() { Host = "old-imap.example.com", Port = 143 };
		ImapOptions moved = new() { Host = "new-imap.example.com", Port = 143 };

		ImapStatusPoller first = _provider.GetOrCreatePoller("bob@example.com", original, credentials);
		ImapStatusPoller second = _provider.GetOrCreatePoller("bob@example.com", moved, credentials);

		Assert.NotSame(first, second);
	}

	/// <summary>
	///   G22: the poll connection is a per-user resource with the same lifetime as the watchers —
	///   the eviction sweep must drop it once the user has no live session, or the gateway holds an
	///   authenticated IMAP connection per user forever.
	/// </summary>
	[Fact]
	public void TrimUserResources_WithoutTheUser_EvictsThePollConnection()
	{
		BackendCredentials credentials = new("bob@example.com", "same-password");
		ImapOptions options = new() { Host = "imap.example.com", Port = 143 };

		ImapStatusPoller before = _provider.GetOrCreatePoller("bob@example.com", options, credentials);
		_provider.TrimUserResources(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alice@example.com" });
		ImapStatusPoller after = _provider.GetOrCreatePoller("bob@example.com", options, credentials);

		Assert.NotSame(before, after);
	}

	/// <summary>
	///   G27: <see cref="ImapBackendProvider.SnapshotWatchers" /> (feeding the admin dashboard) and
	///   the constructor's <c>activesync_idle_watchers</c> gauge callback filtered on
	///   <c>Lazy.IsValueCreated</c> only — but <see cref="ImapBackendProvider.GetOrCreateWatcher" />
	///   dereferences <c>current.Value</c> on EVERY call just to compare Credentials/Options, so the
	///   <c>Lazy&lt;ImapIdleWatcher&gt;</c> is always materialized. <see cref="ImapIdleWatcher" /> only
	///   opens its connection when <c>EnsureStarted</c> runs, from the first
	///   <see cref="ImapIdleWatcher.WaitForChangeAsync" /> — so a user who Syncs (which resolves a
	///   watcher via <c>GetOrCreateWatcher</c>) but never Pings (which is the only thing that calls
	///   <c>WaitForChangeAsync</c>) inflated the count of a connection that was never actually opened.
	/// </summary>
	[Fact]
	public void SnapshotWatchers_ExcludesAMaterializedButNeverStartedWatcher()
	{
		BackendCredentials credentials = new("bob@example.com", "pw");
		ImapOptions options = new() { Host = "imap.example.com", Port = 143 };

		// Materializes the Lazy<ImapIdleWatcher> (GetOrCreateWatcher dereferences .Value) without
		// ever calling WaitForChangeAsync -- exactly a Sync with no matching Ping yet.
		ImapIdleWatcher? watcher = _provider.GetOrCreateWatcher("bob@example.com", options, credentials, "INBOX");
		Assert.NotNull(watcher);

		Assert.Empty(_provider.SnapshotWatchers());
	}

	/// <summary>
	///   G28: <c>TrimUserResources</c>'s <c>key[..key.IndexOf('\n')]</c> has no guard against a
	///   missing separator, unlike the defensive form <see cref="ImapBackendProvider.SnapshotWatchers" />
	///   already uses. Unreachable via <c>GetOrCreateWatcher</c> today (every key it builds is
	///   "user\nfolder"), but the eviction sweep runs on a background timer thread whose escaping
	///   exceptions <c>BackendSessionFactory.EvictIdleSessions</c> explicitly guards against ("an
	///   escaping exception terminates the process") — reproduced by injecting a malformed key
	///   directly into the private watcher dictionary via reflection, since no public path can build
	///   one today.
	/// </summary>
	[Fact]
	public void TrimUserResources_KeyWithoutASeparator_DoesNotThrow()
	{
		ConcurrentDictionary<string, Lazy<ImapIdleWatcher>> watchers =
			(ConcurrentDictionary<string, Lazy<ImapIdleWatcher>>)typeof(ImapBackendProvider)
				.GetField("_watchers", BindingFlags.NonPublic | BindingFlags.Instance)!
				.GetValue(_provider)!;
		watchers["no-separator-key"] = new Lazy<ImapIdleWatcher>(() =>
			new ImapIdleWatcher(new ImapOptions(), new BackendCredentials("x", "y"), "INBOX", NullLogger.Instance));

		Exception? ex = Record.Exception(() =>
			_provider.TrimUserResources(new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

		Assert.Null(ex);
	}
}
