using System.ComponentModel;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Prints the recovery password a device escrowed via Settings→DevicePassword. Only
///   populated when the policy sets PasswordRecoveryEnabled and the device chose to comply.
/// </summary>
internal sealed class DevicePasswordCommand(IAnsiConsole terminal)
	: DatabaseCommand<DevicePasswordCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<user>")]
		[Description("The user owning the device.")]
		public required string User { get; init; }

		[CommandArgument(1, "<deviceId>")]
		[Description("The device id (see 'eas devices').")]
		public required string DeviceId { get; init; }
	}

	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, Settings settings, CancellationToken cancellationToken)
	{
		Device? device = await db.Devices.FirstOrDefaultAsync(
			d => d.UserName == settings.User && d.DeviceId == settings.DeviceId, cancellationToken);
		if (device is null)
		{
			await Console.Error.WriteLineAsync($"No device '{settings.DeviceId}' for '{settings.User}'.");
			return 1;
		}

		if (device.RecoveryPasswordProtected is null)
		{
			await Console.Error.WriteLineAsync(
				"This device has not escrowed a recovery password (requires " +
				"ActiveSync:Policy:PasswordRecoveryEnabled and a compliant client).");
			return 1;
		}

		LocalContentProtector protector = services.GetRequiredService<LocalContentProtector>();
		try
		{
			// Raw password to stdout (pipe-friendly); errors never mix into it.
			await Console.Out.WriteLineAsync(protector.Unprotect(
				device.RecoveryPasswordProtected, device.UserName, "recovery:" + device.DeviceId));
			return 0;
		}
		catch (BackendException ex)
		{
			await Console.Error.WriteLineAsync($"Cannot decrypt the recovery password: {ex.Message}");
			return 1;
		}
	}
}

/// <summary>
///   Flags (or cancels) an account-only remote wipe: the device's next request is herded
///   into Provision, which delivers the 16.1 AccountOnlyRemoteWipe directive; the ack
///   removes the account from the device and auto-blocks the partnership. There is NO
///   full-device wipe — this never factory-resets a phone.
/// </summary>
internal sealed class DeviceWipeCommand(IAnsiConsole terminal)
	: DatabaseCommand<DeviceWipeCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<user>")]
		[Description("The user owning the device.")]
		public required string User { get; init; }

		[CommandArgument(1, "<deviceId>")]
		[Description("The device id (see 'eas devices').")]
		public required string DeviceId { get; init; }

		[CommandOption("--cancel")]
		[Description("Cancel a pending (not yet acknowledged) account wipe.")]
		public bool Cancel { get; init; }
	}

	protected override async Task<int> RunAsync(
		IServiceProvider services, SyncDbContext db, Settings settings, CancellationToken cancellationToken)
	{
		Device? device = await db.Devices.FirstOrDefaultAsync(
			d => d.UserName == settings.User && d.DeviceId == settings.DeviceId, cancellationToken);
		if (device is null)
		{
			await Console.Error.WriteLineAsync($"No device '{settings.DeviceId}' for '{settings.User}'.");
			return 1;
		}

		if (settings.Cancel)
		{
			device.PendingAccountWipe = false;
			await db.SaveChangesAsync(cancellationToken);
			Terminal.WriteLine($"Pending account wipe for {settings.User} ({settings.DeviceId}) cancelled.");
			return 0;
		}

		device.PendingAccountWipe = true;
		await db.SaveChangesAsync(cancellationToken);
		Terminal.WriteLine(
			$"Account wipe armed for {settings.User} ({settings.DeviceId}): the device receives the " +
			"directive on its next request and is blocked once it acknowledges.");
		if (EasVersion.Parse(device.LastProtocolVersion) < EasVersion.V161)
			Terminal.WriteLine(
				$"WARNING: this device last spoke EAS {device.LastProtocolVersion ?? "(unknown)"} — " +
				"account-only wipe requires 16.1. A pre-16.1 client will loop on the Provision " +
				"handshake; use 'eas block' to cut it off instead.");
		return 0;
	}
}
