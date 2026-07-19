using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Command registrations, shared between Program and the CommandAppTester-based unit tests
///   so both run the exact same CLI surface.
/// </summary>
internal static class CliApp
{
	internal static void Configure(IConfigurator config)
	{
		config.SetApplicationName("eas");
		config.AddCommand<ServeCommand>("serve")
			.WithDescription("Start the ActiveSync gateway (accepts --ActiveSync:Section:Key=value overrides).");
		config.AddCommand<HealthcheckCommand>("healthcheck")
			.WithDescription("Probe the running gateway's /healthz endpoint; exit 0 when healthy.");
		config.AddCommand<ProtectCommand>("protect")
			.WithDescription("Seal a secret from stdin with the encryption master key (enc:v1:...).");
		config.AddCommand<HashPasswordCommand>("hash-password")
			.WithDescription("Hash a gateway password from stdin (pbkdf2$...), for per-user overrides.");
		config.AddCommand<UsersCommand>("users")
			.WithDescription("List users that have data in the state database.");
		config.AddCommand<DevicesCommand>("devices")
			.WithDescription("List registered devices, optionally for one user.");
		config.AddCommand<FoldersCommand>("folders")
			.WithDescription("List a user's folder registry.");
		config.AddCommand<ItemsCommand>("items")
			.WithDescription("List a user's local items (metadata only, no decryption).");
		config.AddCommand<ShowCommand>("show")
			.WithDescription("Decrypt and print one local item's raw content.");
		config.AddCommand<BlockCommand>("block")
			.WithDescription("Refuse logins (403) for a user, or for one of their devices.");
		config.AddCommand<UnblockCommand>("unblock")
			.WithDescription("Remove a login block set with 'block'.");
		config.AddBranch("user", user =>
		{
			user.SetDescription("Manage database-declared users (they replace same-login config entries).");
			user.AddCommand<UserListCommand>("list")
				.WithDescription("List all declared users (config + database) with origin and overrides.");
			user.AddCommand<UserShowCommand>("show")
				.WithDescription("Show the effective entry for one login (secrets masked).");
			user.AddCommand<UserAddCommand>("add")
				.WithDescription("Declare a user in the database (copies a same-login config entry).");
			user.AddCommand<UserRemoveCommand>("remove")
				.WithDescription("Delete the database entry (a config entry becomes active again).");
			user.AddCommand<UserSetCommand>("set")
				.WithDescription("Set one field by config path (e.g. Backends:MailStore:Settings:Host); password keys are hashed/sealed.");
			user.AddCommand<UserUnsetCommand>("unset")
				.WithDescription("Clear one field by config path.");
			user.AddCommand<UserPasswordCommand>("password")
				.WithDescription("Set the gateway password from stdin (stored as pbkdf2$ hash).");
			user.AddCommand<UserSecretCommand>("secret")
				.WithDescription("Set a backend password from stdin (stored sealed, enc:v1:).");
		});
		config.AddBranch("device", device =>
		{
			device.SetDescription("Per-device operations (see 'eas devices' for ids).");
			device.AddCommand<DevicePasswordCommand>("password")
				.WithDescription("Print a device's escrowed recovery password (needs PasswordRecoveryEnabled).");
			device.AddCommand<DeviceWipeCommand>("wipe")
				.WithDescription("Arm (or --cancel) a 16.1 account-only wipe: removes the account from the device, never the device itself.");
		});
		config.AddBranch("share", share =>
		{
			share.SetDescription("Grant extra CalDAV collections to users as shared calendar folders.");
			share.AddCommand<ShareAddCommand>("add")
				.WithDescription("Grant a collection to a user (--read-only for gateway-enforced read-only).");
			share.AddCommand<ShareRemoveCommand>("remove")
				.WithDescription("Remove a grant (the folder disappears at the next session build).");
			share.AddCommand<ShareListCommand>("list")
				.WithDescription("List shared-calendar grants, optionally for one user.");
		});
		config.AddBranch("purge", purge =>
		{
			purge.SetDescription("Permanently delete gateway state (asks for confirmation).");
			purge.AddCommand<PurgeUserCommand>("user")
				.WithDescription("Delete ALL state of one user: devices, folders, items, blocks.");
			purge.AddCommand<PurgeDeviceCommand>("device")
				.WithDescription("Delete one device registration (the device re-syncs from scratch).");
		});
	}
}

/// <summary>Default command (no arguments): show the effective-configuration banner and exit.</summary>
internal sealed class BannerCommand : AsyncCommand
{
	protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
		=> await CliVerbs.ShowBannerAsync();
}

/// <summary>
///   `serve` is normally dispatched in Program before Spectre parses (to pass arbitrary
///   configuration override args through); this command keeps it listed in help.
/// </summary>
internal sealed class ServeCommand : AsyncCommand
{
	protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
		=> await Program.RunServerAsync([.. context.Remaining.Raw]);
}

internal sealed class HealthcheckCommand : AsyncCommand
{
	protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
		=> await CliVerbs.HealthcheckAsync();
}

/// <summary>Like serve, normally dispatched pre-parse; registered so help lists it.</summary>
internal sealed class ProtectCommand : AsyncCommand
{
	protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
		=> await CliVerbs.ProtectAsync([.. context.Remaining.Raw]);
}

internal sealed class HashPasswordCommand : AsyncCommand
{
	protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
		=> await CliVerbs.HashPasswordAsync();
}
