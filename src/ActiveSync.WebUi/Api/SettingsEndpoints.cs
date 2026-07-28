using ActiveSync.Core.Administration;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   The global settings editor — the web face of `eas config list/get/set/unset`, driven by
///   the same <see cref="SettingKeys" /> catalogue and <see cref="GlobalSettingStore" />
///   write path (validate, persist, running gateways apply live within ~1 s). Every entry
///   reports its default and SOURCE (default / config file / database) so the UI can render
///   unset values as dimmed placeholders. Secret-flagged values are never echoed back.
/// </summary>
internal static class SettingsEndpoints
{
	internal sealed record SettingDto(
		string Key, string Type, string Tier, string? Default, string Help,
		string[]? EnumValues, long? Min, long? Max, bool Secret,
		string? Value, string Source);

	internal sealed record SettingWriteRequest(string? Value);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("settings", async (GlobalSettingStore store, IConfiguration config, CancellationToken ct) =>
		{
			Dictionary<string, string?> db = new(
				await store.LoadAllAsync(ct), StringComparer.OrdinalIgnoreCase);

			List<SettingDto> entries = [];
			HashSet<string> shown = new(StringComparer.OrdinalIgnoreCase);
			foreach (SettingKeys.SettingKey key in SettingKeys.All)
			{
				entries.Add(Describe(key, db, config));
				shown.Add(key.Key);
			}

			// Any other stored keys not in the static catalogue (stray/legacy overrides) are
			// surfaced so they can be cleared. Backend role settings are DELIBERATELY excluded —
			// they have their own structured "Backends" page and must not appear as raw key/value
			// rows here.
			SortedSet<string> extra = new(db.Keys, StringComparer.OrdinalIgnoreCase);
			extra.ExceptWith(shown);
			foreach (string key in extra)
			{
				if (IsBackendKey(key))
					continue;
				// `SettingKeys.Find` can never return non-null here — `extra` already excludes
				// every catalogue key, and its only other non-null shape (a backend leaf) is excluded
				// above — so this used to be dead code: a row for a key the catalogue no longer
				// recognizes (e.g. `ActiveSync:RequireDeclaredUsers`, removed when `AutoProvisionUsers`
				// took over meaning "refuse undeclared logins") was invisible in the UI and clearable
				// only by guessing the DELETE URL. Surface it as a synthetic, read-only-but-deletable
				// entry instead.
				entries.Add(SettingKeys.Find(key) is { } definition
					? Describe(definition, db, config)
					: new SettingDto(
						key, "String", "live", null, "Unrecognized override — safe to clear.",
						null, null, null, false, db[key], "db"));
			}

			return Results.Ok(entries);
		});

		api.MapPut("settings/{**key}", async (
			string key, SettingWriteRequest request, GlobalSettingStore store,
			IOptions<ActiveSyncOptions> options, BackendProviderRegistry registry,
			IConfiguration config, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.Value))
				return EndpointHelpers.BadRequest("value is required (DELETE clears an override)");
			if (SettingKeys.HostControlledReason(key) is { } refusal)
				return Results.BadRequest(new { error = refusal });
			SettingKeys.SettingKey? definition = SettingKeys.Find(key);
			if (definition is null)
				return EndpointHelpers.BadRequest($"'{key}' is not a recognized setting");
			// Backend leafs are strings to the catalogue; their provider knows their real shape.
			// The configuration here already carries the database layer, so it IS the effective value.
			// Catalogue keys also run the startup validator so a delayed-brick value — one the
			// catalogue accepts but ActiveSyncOptionsValidator would reject at boot — is refused now.
			if ((SettingKeys.Validate(definition, request.Value) ??
			     BackendKeyValidator.Validate(registry, config, key, request.Value) ??
			     (SettingKeys.IsCatalogueKey(definition.Key)
				      ? SettingKeys.ValidateStartupImpact(config, definition.Key, request.Value)
				      : null))
			    is { } validationError)
				return EndpointHelpers.BadRequest(validationError);

			// Catalogue-level secrets (the OIDC client secret, the TLS certificate password) are
			// sealed at rest when the master key exists; open-ended backend keys stay raw (their
			// providers read them verbatim — the synthetic Secret flag only masks display). Shared
			// with `eas config set` via UserSecretPolicy so both surfaces seal identically.
			string value = request.Value;
			if (definition.Secret && SettingKeys.IsCatalogueKey(definition.Key))
			{
				UserSecretPolicy.SecretResult prepared =
					UserSecretPolicy.PrepareCatalogueSecret(value, options.Value.Encryption, definition.Key);
				if (prepared.Error is not null)
					return EndpointHelpers.BadRequest(prepared.Error);
				value = prepared.Value!;
			}

			await store.UpsertAsync(definition.Key, value, ct);
			return Results.Ok(new { key = definition.Key, tier = definition.Tier });
		});

		api.MapDelete("settings/{**key}", async (
			string key, GlobalSettingStore store, IConfiguration config, BackendProviderRegistry registry,
			IServiceProvider services, CancellationToken ct) =>
		{
			// Find the stored key case-insensitively so casing differences don't leave a stale row.
			Dictionary<string, string?> db = await store.LoadAllAsync(ct);
			string? stored = db.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
			if (stored is null)
				return Results.Ok(new { key, tier = SettingKeys.Find(key)?.Tier ?? "live", removed = false });

			// A removal is validated exactly like a write — `DELETE` must not persist a
			// configuration the next start refuses to boot on. `config` already carries the database
			// layer (it IS the effective value everywhere else in this file), so the layer BENEATH it
			// — what a real removal would fall back to — is whatever remains once the database
			// provider itself is excluded. Resolved via IServiceProvider (rather than a plain
			// constructor parameter) because it is not registered in every host that maps this
			// endpoint (e.g. a lean test host with no live settings pipeline) — a missing registration
			// there just means the database layer was never mixed into `config` to begin with.
			DbSettingsConfigurationProvider? dbSettings = services.GetService<DbSettingsConfigurationProvider>();
			IConfiguration fileConfig = dbSettings is not null && config is IConfigurationRoot root
				? new ConfigurationRoot(root.Providers.Where(p => !ReferenceEquals(p, dbSettings)).ToList())
				: config;
			if (SettingKeys.ValidateRemovalImpact(fileConfig, db, registry, stored) is { } error)
				return EndpointHelpers.BadRequest(error);

			await store.DeleteAsync(stored, ct);
			// Report the source the NEXT read would show rather than assuming "default" —
			// `fileConfig` already excludes the database provider, so it IS what remains once this
			// row is gone. Without this the UI badge lied whenever the config file still supplied a
			// value for the same key (it showed "default" until the page was reloaded).
			string source = fileConfig[stored] is not null ? "config" : "default";
			return Results.Ok(new
			{
				key = stored, tier = SettingKeys.Find(stored)?.Tier ?? "live", removed = true, source
			});
		});
	}

	// Backend role settings (ActiveSync:Backends:<Role>:*) are owned by the structured Backends
	// page, not the raw settings grid.
	private static bool IsBackendKey(string key) =>
		key.StartsWith("ActiveSync:Backends:", StringComparison.OrdinalIgnoreCase);

	/// <summary>Effective value + source: database wins, then config file/env, then the code default.</summary>
	private static SettingDto Describe(
		SettingKeys.SettingKey key, Dictionary<string, string?> db, IConfiguration config)
	{
		string? value;
		string source;
		if (db.TryGetValue(key.Key, out string? dbValue))
		{
			value = dbValue;
			source = "db";
		}
		else if (config[key.Key] is { } fileValue)
		{
			value = fileValue;
			source = "config";
		}
		else
		{
			// No explicit value anywhere: the UI shows the default as a placeholder.
			value = null;
			source = "default";
		}

		return new SettingDto(
			key.Key, key.Type.ToString(), key.Tier, key.Default, key.Help,
			key.EnumValues, key.Min, key.Max, key.Secret,
			key.Secret && value is not null ? "***" : value, source);
	}
}
