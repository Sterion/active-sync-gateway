using System.ComponentModel;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
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
		LoginBlock? existing = await db.LoginBlocks.FirstOrDefaultAsync(
			b => b.UserName == settings.User && b.DeviceId == settings.DeviceId, cancellationToken);
		if (existing is not null)
		{
			Terminal.WriteLine($"Already blocked: {settings.Scope} (since {Utc(existing.CreatedUtc)} UTC).");
			return 0;
		}

		// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
		// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
		db.LoginBlocks.Add(new LoginBlock
		{
			UserName = settings.User,
			DeviceId = settings.DeviceId,
			CreatedUtc = DateTime.UtcNow,
		});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(cancellationToken);
		Terminal.WriteLine($"Blocked {settings.Scope} — the gateway now answers its logins with 403.");
		return 0;
	}
}

internal sealed class UnblockCommand(IAnsiConsole terminal) : DatabaseCommand<BlockSettings>(terminal)
{
	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, BlockSettings settings, CancellationToken cancellationToken)
	{
		LoginBlock? existing = await db.LoginBlocks.FirstOrDefaultAsync(
			b => b.UserName == settings.User && b.DeviceId == settings.DeviceId, cancellationToken);
		if (existing is null)
		{
			Terminal.WriteLine($"No block exists for {settings.Scope} — nothing to remove.");
			return 0;
		}

		db.LoginBlocks.Remove(existing);
		await db.SaveChangesAsync(cancellationToken);

		int remaining = await db.LoginBlocks.CountAsync(b => b.UserName == settings.User, cancellationToken);
		Terminal.WriteLine($"Unblocked {settings.Scope}."
			+ (remaining > 0 ? $" {remaining} other block(s) for this user remain." : ""));
		return 0;
	}
}
