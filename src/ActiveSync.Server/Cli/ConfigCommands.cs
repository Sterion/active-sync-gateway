using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Base for the `eas config` commands: builds the lean provider (DbContext, settings store and
///   the backend providers for their own key validation — but nothing that PARSES the current
///   configuration, so it works even against a broken or unconfigured gateway) and
///   hands the command body the settings store plus the file/env configuration (used to label
///   whether an effective value comes from the database, a config file, or the code default).
/// </summary>
internal abstract class SettingsCommandBase<TSettings>(IAnsiConsole terminal) : AsyncCommand<TSettings>
	where TSettings : CommandSettings
{
	protected IAnsiConsole Terminal { get; } = terminal;

	protected sealed override async Task<int> ExecuteAsync(
		CommandContext context, TSettings settings, CancellationToken cancellationToken)
	{
		ServiceProvider? services = await CliServices.TryCreateLeanAsync();
		if (services is null)
			return 1;
		await using ServiceProvider _ = services;
		GlobalSettingStore store = services.GetRequiredService<GlobalSettingStore>();
		IConfiguration fileConfig = services.GetRequiredService<IConfiguration>();
		Registry = services.GetRequiredService<BackendProviderRegistry>();
		return await RunAsync(store, fileConfig, settings, cancellationToken);
	}

	/// <summary>The registered backend providers, for validating their own settings keys.</summary>
	protected BackendProviderRegistry Registry { get; private set; } = null!;

	protected abstract Task<int> RunAsync(
		GlobalSettingStore store, IConfiguration fileConfig, TSettings settings, CancellationToken cancellationToken);

	/// <summary>Effective value + source for one key: database wins, then config file/env, then the code default.</summary>
	protected static (string Value, string Source) Effective(
		string key, string? codeDefault, IReadOnlyDictionary<string, string?> dbValues, IConfiguration fileConfig)
	{
		if (dbValues.TryGetValue(key, out string? dbValue))
			return (dbValue ?? "(unset)", "db");
		string? fileValue = fileConfig[key];
		if (fileValue is not null)
			return (fileValue, "config");
		return (codeDefault ?? "(unset)", "default");
	}

	protected static string PickupNote(bool restart) => restart
		? "Restart the gateway for this to take effect."
		: "A running gateway applies this within ~1s.";

	/// <summary>Plain-text row cells — Text, not markup, so user-supplied strings render verbatim.</summary>
	protected static void AddRow(Table table, params string[] cells)
		=> table.AddRow(cells.Select(c => new Text(c)).ToArray());

	/// <summary>Masks a secret-flagged key's value; unset values stay readable.</summary>
	protected static string Mask(SettingKeys.SettingKey? definition, string value)
		=> definition is { Secret: true } && value != "(unset)" ? "***" : value;
}

internal sealed class ConfigListCommand(IAnsiConsole terminal) : SettingsCommandBase<ConfigListCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings;

	protected override async Task<int> RunAsync(
		GlobalSettingStore store, IConfiguration fileConfig, Settings settings, CancellationToken cancellationToken)
	{
		Dictionary<string, string?> db = new(
			await store.LoadAllAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

		Table table = new Table().AddColumns("Key", "Value", "Source", "Tier");
		HashSet<string> shown = new(StringComparer.OrdinalIgnoreCase);
		foreach (SettingKeys.SettingKey key in SettingKeys.All)
		{
			(string value, string source) = Effective(key.Key, key.Default, db, fileConfig);
			AddRow(table, key.Key, Mask(key, value), source, key.Tier);
			shown.Add(key.Key);
		}

		// Open-ended backend settings (and any other stored keys) aren't in the static catalogue —
		// gather them from the file/env backend section and the database rows.
		SortedSet<string> extra = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string leafKey, string? leafValue) in
		         fileConfig.GetSection("ActiveSync:Backends").AsEnumerable(false))
			if (leafValue is not null)
				extra.Add(leafKey);
		foreach (string dbKey in db.Keys)
			extra.Add(dbKey);
		extra.ExceptWith(shown);

		foreach (string key in extra)
		{
			(string value, string source) = Effective(key, null, db, fileConfig);
			SettingKeys.SettingKey? definition = SettingKeys.Find(key);
			AddRow(table, key, Mask(definition, value), source, definition?.Tier ?? "live");
		}

		Terminal.Write(table);
		return 0;
	}
}

internal sealed class ConfigGetCommand(IAnsiConsole terminal) : SettingsCommandBase<ConfigGetCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<key>")]
		[System.ComponentModel.Description("The configuration path, e.g. ActiveSync:ReadOnly.")]
		public required string Key { get; init; }
	}

	protected override async Task<int> RunAsync(
		GlobalSettingStore store, IConfiguration fileConfig, Settings settings, CancellationToken cancellationToken)
	{
		Dictionary<string, string?> db = new(
			await store.LoadAllAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
		SettingKeys.SettingKey? definition = SettingKeys.Find(settings.Key);
		(string value, string source) = Effective(settings.Key, definition?.Default, db, fileConfig);

		if (source == "default" && definition is null && !db.ContainsKey(settings.Key))
		{
			await Console.Error.WriteLineAsync(
				$"'{settings.Key}' is not a recognized setting and has no stored value (see 'eas config list').");
			return 1;
		}

		Terminal.WriteLine($"{settings.Key} = {Mask(definition, value)}  (source: {source}"
		                   + (definition is not null ? $"; tier: {definition.Tier}" : "") + ")");
		return 0;
	}
}

internal sealed class ConfigSetCommand(IAnsiConsole terminal) : SettingsCommandBase<ConfigSetCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<key>")]
		[System.ComponentModel.Description("The configuration path, e.g. ActiveSync:Eas:MaxHeartbeatSeconds.")]
		public required string Key { get; init; }

		[CommandArgument(1, "<value>")]
		public required string Value { get; init; }
	}

	protected override async Task<int> RunAsync(
		GlobalSettingStore store, IConfiguration fileConfig, Settings settings, CancellationToken cancellationToken)
	{
		if (SettingKeys.IsBootstrap(settings.Key))
		{
			await Console.Error.WriteLineAsync(
				$"'{settings.Key}' is a bootstrap setting — Database and Encryption must come from the " +
				"environment or a config file (they are needed to open and decrypt the database).");
			return 1;
		}

		SettingKeys.SettingKey? definition = SettingKeys.Find(settings.Key);
		if (definition is null)
		{
			await Console.Error.WriteLineAsync(
				$"'{settings.Key}' is not a recognized setting (see 'eas config list' for the catalogue).");
			return 1;
		}

		// The role's provider is usually a stored override, so the lookup has to see the
		// database — this command's configuration is the file/env layer only.
		Dictionary<string, string?> stored = new(
			await store.LoadAllAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
		string? error = SettingKeys.Validate(definition, settings.Value) ??
		                BackendKeyValidator.Validate(Registry,
			                key => stored.TryGetValue(key, out string? v) ? v : fileConfig[key],
			                settings.Key, settings.Value);
		if (error is not null)
		{
			await Console.Error.WriteLineAsync(error);
			return 1;
		}

		await store.UpsertAsync(definition.Key, settings.Value, cancellationToken);
		Terminal.WriteLine($"Set {definition.Key} = {settings.Value}. {PickupNote(definition.Restart)}");
		return 0;
	}
}

internal sealed class ConfigUnsetCommand(IAnsiConsole terminal) : SettingsCommandBase<ConfigUnsetCommand.Settings>(terminal)
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument(0, "<key>")]
		[System.ComponentModel.Description("The configuration path to clear from the database.")]
		public required string Key { get; init; }
	}

	protected override async Task<int> RunAsync(
		GlobalSettingStore store, IConfiguration fileConfig, Settings settings, CancellationToken cancellationToken)
	{
		// Find the stored key case-insensitively so casing differences don't leave a stale row.
		Dictionary<string, string?> db = await store.LoadAllAsync(cancellationToken);
		string? stored = db.Keys.FirstOrDefault(k => k.Equals(settings.Key, StringComparison.OrdinalIgnoreCase));
		if (stored is null)
		{
			Terminal.WriteLine($"{settings.Key} has no database value (already using config/default).");
			return 0;
		}

		await store.DeleteAsync(stored, cancellationToken);
		bool restart = SettingKeys.Find(stored)?.Restart ?? false;
		Terminal.WriteLine($"Unset {stored}. {PickupNote(restart)}");
		return 0;
	}
}
