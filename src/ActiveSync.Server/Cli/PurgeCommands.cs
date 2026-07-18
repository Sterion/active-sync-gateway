using System.ComponentModel;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
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

	protected abstract Task<List<(string Table, int Count)>> DeleteAsync(
		SyncDbContext db, TSettings settings, CancellationToken cancellationToken);

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

		List<(string Table, int Count)> deleted = await DeleteAsync(db, settings, cancellationToken);
		if (deleted.All(d => d.Count == 0))
		{
			Terminal.WriteLine($"Nothing to delete for {Describe(settings)}.");
			return 0;
		}

		Terminal.WriteLine($"Deleted {Describe(settings)}:");
		foreach ((string table, int count) in deleted.Where(d => d.Count > 0))
			Terminal.WriteLine($"  {table}: {count} row(s)");
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

	protected override async Task<List<(string, int)>> DeleteAsync(
		SyncDbContext db, Settings settings, CancellationToken ct)
	{
		string user = settings.User;
		// Children are counted before the parents delete them via ON DELETE CASCADE.
		int deviceFolders = await db.DeviceFolders.CountAsync(f => f.Device.UserName == user, ct);
		int collections = await db.CollectionStates.CountAsync(c => c.Device.UserName == user, ct);
		int davItems = await db.DavItems.CountAsync(i => i.Folder.UserName == user, ct);

		int devices = await db.Devices.Where(d => d.UserName == user).ExecuteDeleteAsync(ct);
		int folders = await db.UserFolders.Where(f => f.UserName == user).ExecuteDeleteAsync(ct);
		int items = await db.LocalItems.Where(i => i.UserName == user).ExecuteDeleteAsync(ct);
		int blocks = await db.LoginBlocks.Where(b => b.UserName == user).ExecuteDeleteAsync(ct);

		return
		[
			("Devices", devices), ("DeviceFolders", deviceFolders), ("CollectionStates", collections),
			("UserFolders", folders), ("DavItems", davItems), ("LocalItems", items), ("LoginBlocks", blocks),
		];
	}
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

	protected override async Task<List<(string, int)>> DeleteAsync(
		SyncDbContext db, Settings settings, CancellationToken ct)
	{
		int deviceFolders = await db.DeviceFolders.CountAsync(
			f => f.Device.UserName == settings.User && f.Device.DeviceId == settings.DeviceId, ct);
		int collections = await db.CollectionStates.CountAsync(
			c => c.Device.UserName == settings.User && c.Device.DeviceId == settings.DeviceId, ct);
		int devices = await db.Devices
			.Where(d => d.UserName == settings.User && d.DeviceId == settings.DeviceId)
			.ExecuteDeleteAsync(ct);
		int blocks = await db.LoginBlocks
			.Where(b => b.UserName == settings.User && b.DeviceId == settings.DeviceId)
			.ExecuteDeleteAsync(ct);

		return
		[
			("Devices", devices), ("DeviceFolders", deviceFolders),
			("CollectionStates", collections), ("LoginBlocks", blocks),
		];
	}
}
