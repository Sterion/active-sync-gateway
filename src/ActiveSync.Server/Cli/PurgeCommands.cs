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

	/// <summary>The (user, device) this command targets, for the impact count.</summary>
	protected abstract (string User, string? DeviceId) Target(TSettings settings);

	protected sealed override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, TSettings settings, CancellationToken cancellationToken)
	{
		DeviceAdminService devices = services.GetRequiredService<DeviceAdminService>();
		// Counted unconditionally — including on the --yes call — so a confirmed purge RE-CHECKS
		// the impact rather than trusting the count from the asking round-trip. CliConfirmation's own
		// type doc requires this: "the operator confirmed a specific loss, not an open-ended one."
		(string user, string? deviceId) = Target(settings);
		DeviceAdminService.DeletionImpact impact =
			await devices.CountDeletionImpactAsync(user, deviceId, cancellationToken);

		if (!settings.Yes)
		{
			// Ask naming what is actually at stake. The question goes back to the CLIENT when this
			// command was forwarded (the captured console cannot prompt), which is what makes
			// `eas purge` work over /cli at all — it used to fail outright telling the operator to
			// pass --yes.
			string question = impact.DestroysContent
				? $"Permanently delete {Describe(settings)}? This destroys {impact.DescribeContent()} " +
				  "which exist nowhere else."
				: $"Permanently delete {Describe(settings)}?";

			// Forwarded: the client asks and re-sends. Nothing has been deleted.
			if (CliConfirmation.CanAsk)
			{
				CliConfirmation.Ask(question);
				return 1;
			}

			if (!Terminal.Profile.Capabilities.Interactive)
			{
				await Console.Error.WriteLineAsync(
					$"{question}\nThis permanently deletes data; confirm with --yes when running non-interactively.");
				return 1;
			}

			if (!await Terminal.ConfirmAsync(question, false, cancellationToken))
			{
				Terminal.WriteLine("Aborted; nothing was deleted.");
				return 1;
			}
		}
		else if (impact.DestroysContent)
		{
			// Re-checked on the confirmed call, as above: name the loss again rather than deleting
			// silently against whatever exists now.
			Terminal.WriteLine($"Deleting {impact.DescribeContent()} along with {Describe(settings)}.");
		}

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

	protected override (string User, string? DeviceId) Target(Settings settings) => (settings.User, null);

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

	protected override (string User, string? DeviceId) Target(Settings settings)
		=> (settings.User, settings.DeviceId);

	protected override Task<IReadOnlyList<DeviceAdminService.PurgeCount>> DeleteAsync(
		DeviceAdminService devices, Settings settings, CancellationToken ct)
		=> devices.PurgeAsync(settings.User, settings.DeviceId, ct);
}
