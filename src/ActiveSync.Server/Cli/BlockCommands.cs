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
	[Description("The user the block applies to.")]
	public required string User { get; init; }

	[CommandArgument(1, "[deviceId]")]
	[Description("Block only this device; omit to block the whole user.")]
	public string? DeviceId { get; init; }

	public string Scope => DeviceId is null ? $"user '{User}'" : $"device '{DeviceId}' of '{User}'";
}

/// <summary>Refuse logins (HTTP 403 after auth) for a user or a single device.</summary>
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

		await devices.BlockAsync(settings.User, settings.DeviceId, cancellationToken);
		Terminal.WriteLine($"Blocked {settings.Scope} — the gateway now answers its logins with 403.");
		return 0;
	}
}

internal sealed class UnblockCommand(IAnsiConsole terminal) : DatabaseCommand<BlockSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, BlockSettings settings, CancellationToken cancellationToken)
	{
		DeviceAdminService devices = services.GetRequiredService<DeviceAdminService>();
		DeviceAdminService.UnblockResult result =
			await devices.UnblockAsync(settings.User, settings.DeviceId, cancellationToken);
		if (!result.Removed)
		{
			Terminal.WriteLine($"No block exists for {settings.Scope} — nothing to remove.");
			return 0;
		}

		Terminal.WriteLine($"Unblocked {settings.Scope}."
			+ (result.RemainingForUser > 0 ? $" {result.RemainingForUser} other block(s) for this user remain." : ""));
		return 0;
	}
}
