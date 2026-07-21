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
				if (!IsBackendKey(key) && SettingKeys.Find(key) is { } definition)
					entries.Add(Describe(definition, db, config));

			return Results.Ok(entries);
		});

		api.MapPut("settings/{**key}", async (
			string key, SettingWriteRequest request, GlobalSettingStore store,
			IOptions<ActiveSyncOptions> options, BackendProviderRegistry registry,
			IConfiguration config, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.Value))
				return EndpointHelpers.BadRequest("value is required (DELETE clears an override)");
			if (SettingKeys.IsBootstrap(key))
				return Results.BadRequest(new
				{
					error = "bootstrap settings (Database, Encryption) must come from the environment " +
					        "or a config file — they are needed to open and decrypt the database"
				});
			SettingKeys.SettingKey? definition = SettingKeys.Find(key);
			if (definition is null)
				return EndpointHelpers.BadRequest($"'{key}' is not a recognized setting");
			// Backend leafs are strings to the catalogue; their provider knows their real shape.
			// The configuration here already carries the database layer, so it IS the effective value.
			if ((SettingKeys.Validate(definition, request.Value) ??
			     BackendKeyValidator.Validate(registry, k => config[k], key, request.Value))
			    is { } validationError)
				return EndpointHelpers.BadRequest(validationError);

			// Catalogue-level secrets (the OIDC client secret) are sealed at rest when the
			// master key exists; open-ended backend keys stay raw (their providers read them
			// verbatim — the synthetic Secret flag only masks display).
			string value = request.Value;
			if (definition.Secret && IsCatalogueKey(definition.Key) && !SecretValue.IsSealed(value))
			{
				byte[]? masterKey = EncryptionKeyLoader.TryLoadKey(options.Value.Encryption, out _);
				if (masterKey is not null)
				{
					value = SecretValue.Seal(value, masterKey);
					System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKey);
				}
			}

			await store.UpsertAsync(definition.Key, value, ct);
			return Results.Ok(new { key = definition.Key, tier = definition.Tier });
		});

		api.MapDelete("settings/{**key}", async (string key, GlobalSettingStore store, CancellationToken ct) =>
		{
			// Find the stored key case-insensitively so casing differences don't leave a stale row.
			Dictionary<string, string?> db = await store.LoadAllAsync(ct);
			string? stored = db.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
			if (stored is null)
				return Results.Ok(new { key, tier = SettingKeys.Find(key)?.Tier ?? "live", removed = false });
			await store.DeleteAsync(stored, ct);
			return Results.Ok(new { key = stored, tier = SettingKeys.Find(stored)?.Tier ?? "live", removed = true });
		});
	}

	private static bool IsCatalogueKey(string key)
	{
		return SettingKeys.All.Any(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
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
