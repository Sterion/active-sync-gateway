using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Builds the non-Kestrel service provider for database-touching CLI commands: validated
///   options plus the provider-matched DbContext and the content protector, wired exactly
///   like the web host (AddSyncDatabase / AddLocalContentProtection).
/// </summary>
internal static class CliServices
{
	internal static async Task<ServiceProvider?> TryCreateAsync()
	{
		IConfigurationRoot config = CliVerbs.BuildConfiguration([]);
		ActiveSyncOptions? options = config.GetSection("ActiveSync").Get<ActiveSyncOptions>();
		if (options is null)
		{
			await Console.Error.WriteLineAsync(
				"Missing 'ActiveSync' configuration section (appsettings.json or environment variables).");
			return null;
		}

		ValidateOptionsResult validation = new ActiveSyncOptionsValidator().Validate(null, options);
		if (validation.Failed)
		{
			await Console.Error.WriteLineAsync("Configuration is invalid:");
			foreach (string failure in validation.Failures ?? [])
				await Console.Error.WriteLineAsync($"  - {failure}");
			return null;
		}

		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IConfiguration>(config);
		services.AddOptions<ActiveSyncOptions>().Bind(config.GetSection("ActiveSync"));
		services.AddSyncDatabase(PostgresConnectionUri.EffectiveProvider(options.Database));
		services.AddLocalContentProtection();
		services.AddSingleton<ActiveSync.Backends.Local.LocalChangeNotifier>();
		services.AddBackendProviders();
		try
		{
			// Load plugins so `eas user` validation sees plugin-provided backends too.
			ActiveSync.Core.Plugins.PluginLoader.LoadInto(services, config,
				Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
		}
		catch (InvalidOperationException ex)
		{
			await Console.Error.WriteLineAsync($"Plugin load failed: {ex.Message}");
			return null;
		}

		services.AddSingleton<ActiveSync.Core.Accounts.AccountStore>();
		ServiceProvider provider = services.BuildServiceProvider();

		// `eas user` writes are validated with the exact rules serve applies at startup —
		// a broken role section must surface here, not corrupt an account row.
		try
		{
			provider.GetRequiredService<BackendConfigurationValidator>().Validate();
		}
		catch (InvalidOperationException ex)
		{
			await Console.Error.WriteLineAsync(ex.Message);
			await provider.DisposeAsync();
			return null;
		}

		// CLI commands never migrate; refuse to query a missing or outdated schema instead of
		// failing with a provider error mid-command.
		try
		{
			await using AsyncServiceScope scope = provider.CreateAsyncScope();
			SyncDbContext db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
			List<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
			if (pending.Count > 0)
			{
				await Console.Error.WriteLineAsync(
					$"The state database is missing {pending.Count} migration(s) — " +
					"start the gateway once ('eas serve') to create/upgrade the schema.");
				await provider.DisposeAsync();
				return null;
			}
		}
		catch (Exception ex)
		{
			await Console.Error.WriteLineAsync($"Cannot open the state database: {ex.Message}");
			await provider.DisposeAsync();
			return null;
		}

		return provider;
	}

	/// <summary>
	///   Lean provider for the settings/logs commands: configuration + the provider-matched
	///   DbContext and the <see cref="GlobalSettingStore" /> only — NO backend providers, plugins
	///   or configuration validation. Skipping validation is deliberate: `eas config set` must work
	///   to REPAIR a broken or unconfigured gateway, and reading logs must never depend on valid
	///   backend config. Still refuses a missing/outdated schema (start 'eas serve' once).
	/// </summary>
	internal static async Task<ServiceProvider?> TryCreateLeanAsync()
	{
		IConfigurationRoot config = CliVerbs.BuildConfiguration([]);
		DatabaseOptions database =
			config.GetSection("ActiveSync:Database").Get<DatabaseOptions>() ?? new DatabaseOptions();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<IConfiguration>(config);
		services.AddOptions<ActiveSyncOptions>().Bind(config.GetSection("ActiveSync"));
		services.AddSyncDatabase(PostgresConnectionUri.EffectiveProvider(database));
		services.AddSingleton<GlobalSettingStore>();
		// The providers come along so `eas config set ActiveSync:Backends:...` is checked against
		// the shape the provider actually reads. Registration is lazy: nothing here resolves
		// BackendRolesProvider, which parses the config and would refuse to build on exactly the
		// broken configuration this command exists to repair.
		services.AddLocalContentProtection();
		services.AddSingleton<ActiveSync.Core.Security.GatewayCertificateStore>();
		services.AddSingleton<ActiveSync.Core.Security.TlsCertificateResolver>();
		services.AddSingleton<ActiveSync.Backends.Local.LocalChangeNotifier>();
		services.AddBackendProviders();
		try
		{
			// Plugin providers describe their settings too — validate against theirs as well.
			ActiveSync.Core.Plugins.PluginLoader.LoadInto(services, config,
				Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
		}
		catch (InvalidOperationException)
		{
			// A broken plugin must not block editing settings; `eas serve` reports it properly.
		}

		ServiceProvider provider = services.BuildServiceProvider();

		try
		{
			await using AsyncServiceScope scope = provider.CreateAsyncScope();
			SyncDbContext db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
			List<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
			if (pending.Count > 0)
			{
				await Console.Error.WriteLineAsync(
					$"The state database is missing {pending.Count} migration(s) — " +
					"start the gateway once ('eas serve') to create/upgrade the schema.");
				await provider.DisposeAsync();
				return null;
			}
		}
		catch (Exception ex)
		{
			await Console.Error.WriteLineAsync($"Cannot open the state database: {ex.Message}");
			await provider.DisposeAsync();
			return null;
		}

		return provider;
	}
}
