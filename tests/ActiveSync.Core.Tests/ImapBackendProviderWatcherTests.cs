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
}
