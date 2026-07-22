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
	private readonly ChangeStampRefreshGate _gate = new();
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
		if (!_gate.ShouldCheck(force))
			return;

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

				// B6: record progress BEFORE firing the reload token. SetData swaps the snapshot and
				// then fires the token; a downstream subscriber that throws (e.g. the account-snapshot
				// rebuild) used to escape here — so _lastStamp/_hasLoaded were never set, the same stamp
				// was retried forever, and the failure was mislogged as a settings-refresh error. The
				// data is already applied by then, so a throwing subscriber must not undo our progress.
				_lastStamp = stamp;
				_hasLoaded = true;
				try
				{
					provider.SetData(data);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger?.LogWarning(ex,
						"A settings reload handler failed after the snapshot was applied; the settings " +
						"were still updated");
				}

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
			_gate.ScheduleNext(refreshSeconds);
			_refreshGate.Release();
		}
	}
}
