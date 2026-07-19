using ActiveSync.Core.Settings;
using Microsoft.Extensions.Hosting;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Background poll that drives <see cref="SettingsRefresher" /> so CLI/admin settings changes are
///   picked up by every replica within ~one poll interval (Auth:UsersRefreshSeconds, default 1s)
///   without a restart. The refresher applies its own interval gate and single-flight guard; this
///   service only ticks it.
/// </summary>
public sealed class SettingsRefreshService(SettingsRefresher refresher) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
				await refresher.EnsureFreshAsync(false, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
	}
}
