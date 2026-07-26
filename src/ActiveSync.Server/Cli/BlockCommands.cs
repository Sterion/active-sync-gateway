using System.ComponentModel;
using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

internal sealed class BlockSettings : CommandSettings
{
	[CommandArgument(0, "<user>")]
	[Description("The user owning the device.")]
	public required string User { get; init; }

	[CommandArgument(1, "[deviceId]")]
	[Description("The device to block (required — see 'eas user disable' to disable a whole user).")]
	public string? DeviceId { get; init; }

	public string Scope => $"device '{DeviceId}' of '{User}'";
}

/// <summary>
///   Refuses logins (HTTP 403 after auth) for ONE DEVICE. Deliberately device-scoped: a bare
///   user is an error pointing at <c>eas user disable</c> rather than doing something subtly
///   different. Having both spellings write the same state would put back, in the CLI, exactly
///   the two-mechanisms-one-concept problem the schema removed (db-restructure decision 19).
/// </summary>
internal sealed class BlockCommand(IAnsiConsole terminal) : DatabaseCommand<BlockSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, BlockSettings settings, CancellationToken cancellationToken)
	{
		DeviceAdminService devices = services.GetRequiredService<DeviceAdminService>();
		LoginBlock? existing = await devices.FindBlockAsync(settings.User, settings.DeviceId, cancellationToken);
		if (existing is not null)
		{
			Terminal.WriteLine($"Already blocked: {settings.Scope} (since {Utc(existing.CreatedUtc)} UTC).");
			return 0;
		}

		DeviceAdminService.BlockOutcome outcome =
			await devices.BlockAsync(settings.User, settings.DeviceId, cancellationToken);
		switch (outcome)
		{
			case DeviceAdminService.BlockOutcome.DeviceRequired:
				await Console.Error.WriteLineAsync(
					$"A device id is required: blocks are per-device. To disable the whole user, " +
					$"run 'eas user disable {settings.User}'.");
				return 1;
			case DeviceAdminService.BlockOutcome.UnknownDevice:
				await Console.Error.WriteLineAsync(
					$"No device '{settings.DeviceId}' for '{settings.User}' (see 'eas devices'). " +
					"A device appears once it has synced; to refuse the user before that, " +
					$"run 'eas user disable {settings.User}'.");
				return 1;
			default:
				Terminal.WriteLine($"Blocked {settings.Scope} — the gateway now answers its logins with 403.");
				return 0;
		}
	}
}

internal sealed class UnblockCommand(IAnsiConsole terminal) : DatabaseCommand<BlockSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, BlockSettings settings, CancellationToken cancellationToken)
	{
		DeviceAdminService devices = services.GetRequiredService<DeviceAdminService>();
		(DeviceAdminService.BlockOutcome outcome, int remaining) =
			await devices.UnblockAsync(settings.User, settings.DeviceId, cancellationToken);
		switch (outcome)
		{
			case DeviceAdminService.BlockOutcome.DeviceRequired:
				await Console.Error.WriteLineAsync(
					$"A device id is required: blocks are per-device. To re-enable the whole user, " +
					$"run 'eas user enable {settings.User}'.");
				return 1;
			case DeviceAdminService.BlockOutcome.Applied:
				Terminal.WriteLine($"Unblocked {settings.Scope}."
					+ (remaining > 0 ? $" {remaining} other block(s) for this user remain." : ""));
				return 0;
			default:
				Terminal.WriteLine($"No block exists for {settings.Scope} — nothing to remove.");
				return 0;
		}
	}
}
