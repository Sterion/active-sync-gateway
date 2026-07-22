using ActiveSync.Core.Administration;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Settings;
using ActiveSync.Crypto;
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

	/// <summary>Masks a secret-flagged key's value; unset values stay readable.</summary>
	protected static string Mask(SettingKeys.SettingKey? definition, string value)
		=> definition is { Secret: true } && value != "(unset)" ? SecretRedaction.Mask : value;

	// Markup for the source/tier so they pop when the terminal supports colour (and render as the
	// plain word otherwise). These are fixed tokens, so the markup is safe to embed unescaped.
	protected static string SourceTag(string source) => source switch
	{
		"db" => "[green]db[/]",
		"config" => "[blue]config[/]",
		_ => "[grey]default[/]"
	};

	protected static string TierTag(string tier) =>
		string.Equals(tier, "restart", StringComparison.OrdinalIgnoreCase) ? "[yellow]restart[/]" : "[green]live[/]";
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
			table.AddRow(new Text(key.Key), new Text(Mask(key, value)),
				new Markup(SourceTag(source)), new Markup(TierTag(key.Tier)));
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
			table.AddRow(new Text(key), new Text(Mask(definition, value)),
				new Markup(SourceTag(source)), new Markup(TierTag(definition?.Tier ?? "live")));
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

		// Colour the source/tier tokens; escape the (user-controlled) key + value so their contents
		// can't be read as markup. Renders plain when the terminal has no colour.
		Terminal.MarkupLine($"{Markup.Escape(settings.Key)} = {Markup.Escape(Mask(definition, value))}  (source: {SourceTag(source)}"
		                    + (definition is not null ? $"; tier: {TierTag(definition.Tier)}" : "") + ")");
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
		if (SettingKeys.HostControlledReason(settings.Key) is { } refusal)
		{
			await Console.Error.WriteLineAsync($"'{settings.Key}' cannot be stored: {refusal}.");
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
		// The effective view the next startup would validate: file/env under the database overrides.
		IConfiguration effective = new ConfigurationBuilder()
			.AddConfiguration(fileConfig)
			.AddInMemoryCollection(stored)
			.Build();
		string? error = SettingKeys.Validate(definition, settings.Value) ??
		                BackendKeyValidator.Validate(Registry,
			                key => stored.TryGetValue(key, out string? v) ? v : fileConfig[key],
			                settings.Key, settings.Value) ??
		                (SettingKeys.IsCatalogueKey(definition.Key)
			                ? SettingKeys.ValidateStartupImpact(effective, definition.Key, settings.Value)
			                : null);
		if (error is not null)
		{
			await Console.Error.WriteLineAsync(error);
			return 1;
		}

		// B5: seal a catalogue-level secret at rest the same way the web settings editor does —
		// otherwise `eas config set ActiveSync:WebUi:Oidc:ClientSecret ...` stored it in plaintext
		// while the web UI sealed it. Open-ended backend leaves stay raw (their provider reads them).
		string valueToStore = settings.Value;
		if (definition is { Secret: true } && SettingKeys.IsCatalogueKey(definition.Key))
		{
			EncryptionOptions encryption =
				fileConfig.GetSection("ActiveSync:Encryption").Get<EncryptionOptions>() ?? new EncryptionOptions();
			AccountSecretPolicy.SecretResult prepared =
				AccountSecretPolicy.PrepareCatalogueSecret(settings.Value, encryption, definition.Key);
			if (prepared.Error is not null)
			{
				await Console.Error.WriteLineAsync(prepared.Error);
				return 1;
			}

			valueToStore = prepared.Value!;
		}

		await store.UpsertAsync(definition.Key, valueToStore, cancellationToken);
		Terminal.WriteLine($"Set {definition.Key} = {Mask(definition, settings.Value)}. {PickupNote(definition.Restart)}");
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
