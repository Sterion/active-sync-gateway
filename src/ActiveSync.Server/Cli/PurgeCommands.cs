using System.ComponentModel;
using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

internal abstract class PurgeSettings : CommandSettings
{
	[CommandOption("-y|--yes")]
	[Description("Skip the confirmation prompt (required when not running interactively).")]
	public bool Yes { get; init; }
}

/// <summary>Shared confirm-then-delete flow; delete order relies on DB-level cascades.</summary>
internal abstract class PurgeCommand<TSettings>(IAnsiConsole terminal) : DatabaseCommand<TSettings>(terminal)
	where TSettings : PurgeSettings
{
	protected abstract string Describe(TSettings settings);

	protected abstract Task<IReadOnlyList<DeviceAdminService.PurgeCount>> DeleteAsync(
		DeviceAdminService devices, TSettings settings, CancellationToken cancellationToken);

	protected sealed override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, TSettings settings, CancellationToken cancellationToken)
	{
		if (!settings.Yes)
		{
			if (!Terminal.Profile.Capabilities.Interactive)
			{
				await Console.Error.WriteLineAsync(
					"This permanently deletes data; confirm with --yes when running non-interactively.");
				return 1;
			}

			if (!await Terminal.ConfirmAsync($"Permanently delete {Describe(settings)}?", false, cancellationToken))
			{
				Terminal.WriteLine("Aborted; nothing was deleted.");
				return 1;
			}
		}

		DeviceAdminService devices = services.GetRequiredService<DeviceAdminService>();
		IReadOnlyList<DeviceAdminService.PurgeCount> deleted = await DeleteAsync(devices, settings, cancellationToken);
		if (deleted.All(d => d.Count == 0))
		{
			Terminal.WriteLine($"Nothing to delete for {Describe(settings)}.");
			return 0;
		}

		Terminal.WriteLine($"Deleted {Describe(settings)}:");
		foreach (DeviceAdminService.PurgeCount entry in deleted.Where(d => d.Count > 0))
			Terminal.WriteLine($"  {entry.Table}: {entry.Count} row(s)");
		return 0;
	}
}

internal sealed class PurgeUserCommand(IAnsiConsole terminal) : PurgeCommand<PurgeUserCommand.Settings>(terminal)
{
	public sealed class Settings : PurgeSettings
	{
		[CommandArgument(0, "<user>")]
		[Description("The user whose entire gateway state to delete.")]
		public required string User { get; init; }
	}

	protected override string Describe(Settings settings)
		=> $"ALL gateway state of user '{settings.User}'";

	protected override Task<IReadOnlyList<DeviceAdminService.PurgeCount>> DeleteAsync(
		DeviceAdminService devices, Settings settings, CancellationToken ct)
		=> devices.PurgeAsync(settings.User, null, ct);
}

internal sealed class PurgeDeviceCommand(IAnsiConsole terminal) : PurgeCommand<PurgeDeviceCommand.Settings>(terminal)
{
	public sealed class Settings : PurgeSettings
	{
		[CommandArgument(0, "<user>")]
		public required string User { get; init; }

		[CommandArgument(1, "<deviceId>")]
		[Description("The device registration to delete (its sync state resets to scratch).")]
		public required string DeviceId { get; init; }
	}

	protected override string Describe(Settings settings)
		=> $"device '{settings.DeviceId}' of user '{settings.User}'";

	protected override Task<IReadOnlyList<DeviceAdminService.PurgeCount>> DeleteAsync(
		DeviceAdminService devices, Settings settings, CancellationToken ct)
		=> devices.PurgeAsync(settings.User, settings.DeviceId, ct);
}
