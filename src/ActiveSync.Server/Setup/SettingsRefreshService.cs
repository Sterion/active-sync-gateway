using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Background poll that drives <see cref="SettingsRefresher" /> so CLI/admin settings changes are
///   picked up by every replica within ~one poll interval (Auth:UsersRefreshSeconds, default 1s)
///   without a restart. The refresher applies its own interval gate and single-flight guard; this
///   service only ticks it.
/// </summary>
public sealed class SettingsRefreshService(
	SettingsRefresher refresher,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<SettingsRefreshService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		int failures = 0;
		while (!stoppingToken.IsCancellationRequested)
			try
			{
				double seconds = options.CurrentValue.Auth.UsersRefreshSeconds;
				TimeSpan interval = double.IsFinite(seconds) && seconds > 0
					? TimeSpan.FromSeconds(seconds)
					: TimeSpan.FromSeconds(1);
				await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
				await refresher.EnsureFreshAsync(false, stoppingToken).ConfigureAwait(false);
				failures = 0;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break; // host shutdown — the only cancellation that should stop the poll
			}
			catch (Exception ex)
			{
				// E11: any other fault — including a non-shutdown OperationCanceledException such as an
				// EF command timeout — must NOT stop the poll, or live settings freeze for the process
				// lifetime with no signal. Keep ticking; log the first occurrence and every 60th after
				// (~once a minute at the default interval) so a sustained fault leaves a trail without
				// flooding.
				if (++failures == 1 || failures % 60 == 0)
					logger.LogWarning(ex,
						"Settings refresh tick failed ({Count} consecutive); continuing to poll", failures);
			}
	}
}
