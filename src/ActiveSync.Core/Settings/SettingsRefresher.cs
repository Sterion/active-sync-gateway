using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Settings;

/// <summary>
///   Polls the <see cref="ActiveSync.Core.State.SettingsStamp" /> and, when it moves, reloads the
///   global settings into the <see cref="DbSettingsConfigurationProvider" /> — which fires the
///   configuration reload token so <c>IOptionsMonitor</c> recomputes. Mirrors
///   <see cref="ActiveSync.Core.Accounts.AccountResolver" />'s change-stamp poll: at most one
///   primary-key point-read every <see cref="AuthOptions.UsersRefreshSeconds" />; failures keep the
///   current snapshot (settings never go down with the database). Registered as a singleton and
///   driven by a background service; multi-pod safe (each replica polls its own stamp).
/// </summary>
public sealed class SettingsRefresher(
	GlobalSettingStore store,
	DbSettingsConfigurationProvider provider,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<SettingsRefresher>? logger = null)
{
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private long _nextCheckTicks;
	private Guid? _lastStamp;
	private bool _hasLoaded;
	private bool _refreshErrorLogged;

	/// <summary>Raised after the settings snapshot changed (for consumers beyond IOptions, e.g. session recycle).</summary>
	public event Action? Changed;

	/// <summary>
	///   Reloads settings when the change stamp moved (or on the first call). Cost when idle: one
	///   primary-key point-read at most every UsersRefreshSeconds. Failures keep the current
	///   snapshot. <paramref name="force" /> bypasses the interval gate (used once at startup).
	/// </summary>
	public async Task EnsureFreshAsync(bool force, CancellationToken ct)
	{
		double refreshSeconds = options.CurrentValue.Auth.UsersRefreshSeconds;
		if (!force)
		{
			if (_hasLoaded && refreshSeconds < 0)
				return;
			if (Environment.TickCount64 < Volatile.Read(ref _nextCheckTicks))
				return;
		}

		// A refresh already in flight serves this caller fine — use the current snapshot.
		if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false))
			return;
		try
		{
			Guid? stamp = await store.ReadStampAsync(ct).ConfigureAwait(false);
			if (!_hasLoaded || stamp != _lastStamp)
			{
				Dictionary<string, string?> data = stamp is null
					? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
					: await store.LoadAllAsync(ct).ConfigureAwait(false);
				provider.SetData(data);
				_lastStamp = stamp;
				_hasLoaded = true;
				logger?.LogInformation(
					"Global settings snapshot rebuilt: {Count} database override(s)", data.Count);
				Changed?.Invoke();
			}

			_refreshErrorLogged = false;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			if (!_refreshErrorLogged)
			{
				logger?.LogWarning(ex, "Could not refresh database settings; keeping the current snapshot");
				_refreshErrorLogged = true;
			}
		}
		finally
		{
			Volatile.Write(ref _nextCheckTicks,
				Environment.TickCount64 + (long)(Math.Max(refreshSeconds, 0) * 1000));
			_refreshGate.Release();
		}
	}
}
