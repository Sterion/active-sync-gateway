using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   The Backends editor: which provider fills each role and with what settings, stored as
///   database overrides over the config file through the same <see cref="GlobalSettingStore" />
///   path the settings editor uses (running gateways pick the change up within ~1 s — no
///   restart). The form is rendered from the providers' own
///   <see cref="IBackendProvider.DescribeConfiguration" /> schemas, so the UI never hard-codes
///   a provider's fields and a plugin's are editable the day it ships.
/// </summary>
internal static class BackendsEndpoints
{
	private const string SectionPrefix = "ActiveSync:Backends";
	private const string SecretMask = "***";

	internal sealed record FieldDto(
		string Name, string Label, string Type, bool Required, string? Default,
		IReadOnlyList<string>? EnumValues, string Help, long? Min, long? Max);

	internal sealed record ProviderDto(
		string Name, IReadOnlyList<string> Roles, bool Probe,
		IReadOnlyDictionary<string, IReadOnlyList<FieldDto>> Schemas);

	internal sealed record SettingDto(string Key, string? Value, string Source, bool Secret);

	internal sealed record RoleDto(
		string Role, string? Provider, string ProviderSource, bool Assigned,
		IReadOnlyList<SettingDto> Settings);

	internal sealed record RoleWriteRequest(string? Provider, Dictionary<string, string?>? Settings);

	internal sealed record FailureDto(string? Field, string Message);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("backends/providers", (BackendProviderRegistry registry) =>
		{
			return Results.Ok(registry.All
				.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
				.Select(provider => new ProviderDto(
					provider.Name,
					[.. provider.SupportedRoles.Order().Select(r => r.ToString())],
					provider is IReadinessSource,
					provider.SupportedRoles.ToDictionary(
						role => role.ToString(),
						role => (IReadOnlyList<FieldDto>)
						[
							.. provider.DescribeConfiguration(role).Select(Describe)
						]))));
		});

		api.MapGet("backends", async (
			BackendProviderRegistry registry, GlobalSettingStore store, IConfiguration config,
			CancellationToken ct) =>
		{
			Dictionary<string, string?> db = new(await store.LoadAllAsync(ct), StringComparer.OrdinalIgnoreCase);
			return Results.Ok(Enum.GetValues<BackendRole>().Select(role => Describe(role, registry, db, config)));
		});

		api.MapPut("backends/{role}", async (
			string role, RoleWriteRequest request, BackendProviderRegistry registry,
			GlobalSettingStore store, IConfiguration config, CancellationToken ct) =>
		{
			if (!Enum.TryParse(role, true, out BackendRole parsed))
				return Results.BadRequest(new { error = $"'{role}' is not a backend role" });

			Dictionary<string, string?> db = new(await store.LoadAllAsync(ct), StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string?> merged = Merge(parsed, request, db, config, out string? providerName);

			IBackendProvider? provider = null;
			if (providerName is not null)
			{
				provider = Resolve(registry, providerName, parsed, out string? providerError);
				if (provider is null)
					return Results.BadRequest(new { error = providerError });

				IReadOnlyList<BackendFieldError> errors =
					BackendConfigValidation.Validate(provider, parsed, merged);
				if (errors.Count > 0)
					return Results.BadRequest(new
					{
						error = errors[0].Message,
						failures = errors.Select(e => new FailureDto(e.Field, e.Message))
					});
			}

			await PersistAsync(parsed, request, db, config, store, provider, ct);
			return Results.Ok(Describe(parsed, registry,
				new Dictionary<string, string?>(await store.LoadAllAsync(ct), StringComparer.OrdinalIgnoreCase),
				config));
		});

		api.MapDelete("backends/{role}", async (
			string role, BackendProviderRegistry registry, GlobalSettingStore store,
			IConfiguration config, CancellationToken ct) =>
		{
			if (!Enum.TryParse(role, true, out BackendRole parsed))
				return Results.BadRequest(new { error = $"'{role}' is not a backend role" });

			// Drop every database row for the role: what the config file says takes over again.
			string prefix = $"{SectionPrefix}:{parsed}:";
			Dictionary<string, string?> db = await store.LoadAllAsync(ct);
			int removed = 0;
			foreach (string key in db.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
				if (await store.DeleteAsync(key, ct))
					removed++;

			return Results.Ok(new
			{
				removed,
				role = Describe(parsed, registry,
					new Dictionary<string, string?>(await store.LoadAllAsync(ct), StringComparer.OrdinalIgnoreCase),
					config)
			});
		});

		api.MapPost("backends/{role}/validate", async (
			string role, RoleWriteRequest request, BackendProviderRegistry registry,
			GlobalSettingStore store, IConfiguration config, CancellationToken ct) =>
		{
			if (!Enum.TryParse(role, true, out BackendRole parsed))
				return Results.BadRequest(new { error = $"'{role}' is not a backend role" });

			Dictionary<string, string?> db = new(await store.LoadAllAsync(ct), StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string?> merged = Merge(parsed, request, db, config, out string? providerName);
			if (providerName is null)
				return Results.Ok(new { failures = Array.Empty<FailureDto>() });

			IBackendProvider? provider = Resolve(registry, providerName, parsed, out string? providerError);
			if (provider is null)
				return Results.Ok(new { failures = new[] { new FailureDto("Provider", providerError!) } });

			return Results.Ok(new
			{
				failures = BackendConfigValidation.Validate(provider, parsed, merged)
					.Select(e => new FailureDto(e.Field, e.Message))
			});
		});

		api.MapPost("backends/{role}/test", async (
			string role, RoleWriteRequest request, BackendProviderRegistry registry,
			GlobalSettingStore store, IConfiguration config, ClaimsPrincipal principal,
			ILoggerFactory loggerFactory, CancellationToken ct) =>
		{
			if (!Enum.TryParse(role, true, out BackendRole parsed))
				return Results.BadRequest(new { error = $"'{role}' is not a backend role" });

			Dictionary<string, string?> db = new(await store.LoadAllAsync(ct), StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string?> merged = Merge(parsed, request, db, config, out string? providerName);
			if (providerName is null)
				return Results.Ok(new { supported = false, reachable = false, detail = "No provider selected." });

			IBackendProvider? provider = Resolve(registry, providerName, parsed, out string? providerError);
			if (provider is null)
				return Results.Ok(new { supported = false, reachable = false, detail = providerError! });
			if (provider is not IReadinessSource source)
				return Results.Ok(new
				{
					supported = false, reachable = false,
					detail = $"The {providerName} provider has no reachability probe."
				});

			// Connectivity only — the same probe /readyz uses. With pass-through authentication
			// there are no stored credentials to log in with, so this cannot prove more.
			try
			{
				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeout.CancelAfter(TimeSpan.FromSeconds(5));
				bool reachable = await source.ProbeReadinessAsync(ProviderSettings.FromFlat(merged), timeout.Token);
				return Results.Ok(new
				{
					supported = true, reachable,
					detail = reachable ? "The server answered." : "No answer from the server."
				});
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
			{
				// A closed outcome set, deliberately coarse. The raw exception text distinguishes
				// refused from timed-out from DNS-failure from TLS-mismatch, which turns an
				// admin-only convenience into a precise scanner of whatever network the gateway
				// sits on — and admin here is not always root-equivalent. The full exception goes
				// to the log, attributed to the operator who asked for it.
				loggerFactory.CreateLogger("ActiveSync.WebUi.Backends").LogWarning(ex,
					"Backend probe of the {Role} role ({Provider}) requested by {User} failed",
					parsed, providerName, principal.Identity?.Name ?? "(unknown)");
				return Results.Ok(new { supported = true, reachable = false, detail = Outcome(ex) });
			}
		});
	}

	/// <summary>
	///   The one sentence a failed probe is allowed to say. Every network-level failure collapses
	///   to the same answer on purpose — telling refused from timed-out from name-not-found is
	///   the whole value of a scanner. The timeout is separated only because it is the gateway's
	///   own 5 s cap and says nothing about the target.
	/// </summary>
	private static string Outcome(Exception ex)
	{
		return ex.GetBaseException() switch
		{
			OperationCanceledException or TimeoutException => "The server did not answer in time.",
			_ => "The server could not be reached."
		};
	}

	/// <summary>The provider for a name, or null with the message explaining why not.</summary>
	private static IBackendProvider? Resolve(
		BackendProviderRegistry registry, string providerName, BackendRole role, out string? error)
	{
		try
		{
			error = null;
			return registry.GetFor(providerName, role);
		}
		catch (InvalidOperationException ex)
		{
			error = ex.Message;
			return null;
		}
	}

	/// <summary>
	///   The role's effective settings after applying a submitted change: file config overlaid
	///   with the stored rows, then with the request. A null request value means "remove the
	///   override", so the config value below it resurfaces; the secret mask means "unchanged".
	/// </summary>
	private static Dictionary<string, string?> Merge(
		BackendRole role, RoleWriteRequest request, Dictionary<string, string?> db,
		IConfiguration config, out string? providerName)
	{
		Dictionary<string, string?> effective = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string leaf, string? value) in ConfigLeafs(role, config))
			effective[leaf] = value;
		foreach ((string leaf, string? value) in DbLeafs(role, db))
			effective[leaf] = value;

		foreach ((string leaf, string? value) in request.Settings ?? [])
		{
			if (value == SecretMask)
				continue;
			if (value is null)
				effective.Remove(leaf); // reverts to the config value, re-applied below
			else
				effective[leaf] = value;
		}

		// A removed override falls back to the config file, not to nothing.
		foreach ((string leaf, string? value) in ConfigLeafs(role, config))
			if (!effective.ContainsKey(leaf))
				effective[leaf] = value;

		providerName = request.Provider ?? effective.GetValueOrDefault(BackendRolesConfig.ProviderKey);
		if (string.IsNullOrWhiteSpace(providerName))
			providerName = null;
		effective.Remove(BackendRolesConfig.ProviderKey);
		return effective;
	}

	/// <summary>
	///   Writes the change. A value that matches what the layer below would supply anyway (the
	///   config file, or the provider's own default) is not stored — and an existing row for it
	///   is removed — so an override always means a real deviation.
	/// </summary>
	private static async Task PersistAsync(
		BackendRole role, RoleWriteRequest request, Dictionary<string, string?> db,
		IConfiguration config, GlobalSettingStore store, IBackendProvider? provider,
		CancellationToken ct)
	{
		Dictionary<string, string?> configLeafs = new(ConfigLeafs(role, config), StringComparer.OrdinalIgnoreCase);
		// Below the config file sits the provider's own default, which is just as much "not an
		// override" — storing it would only pin a value that already applies.
		foreach (BackendConfigField field in provider?.DescribeConfiguration(role) ?? [])
			if (field.Default is { } declared && !configLeafs.ContainsKey(field.Name))
				configLeafs[field.Name] = declared;
		Dictionary<string, string?> changes = new(request.Settings ?? [], StringComparer.OrdinalIgnoreCase);
		if (request.Provider is { } requested)
			changes[BackendRolesConfig.ProviderKey] = string.IsNullOrWhiteSpace(requested) ? null : requested;

		foreach ((string leaf, string? value) in changes)
		{
			if (value == SecretMask)
				continue;
			string key = $"{SectionPrefix}:{role}:{leaf}";
			string? stored = db.Keys
				.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
			bool redundant = value is null ||
			                 string.Equals(value, configLeafs.GetValueOrDefault(leaf), StringComparison.Ordinal);

			if (redundant)
			{
				if (stored is not null)
					await store.DeleteAsync(stored, ct);
				continue;
			}

			await store.UpsertAsync(stored ?? key, value!, ct);
		}
	}

	private static RoleDto Describe(
		BackendRole role, BackendProviderRegistry registry,
		Dictionary<string, string?> db, IConfiguration config)
	{
		Dictionary<string, (string? Value, string Source)> leafs = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string leaf, string? value) in ConfigLeafs(role, config))
			leafs[leaf] = (value, "config");
		foreach ((string leaf, string? value) in DbLeafs(role, db))
			leafs[leaf] = (value, "db");

		leafs.Remove(BackendRolesConfig.ProviderKey, out (string? Value, string Source) provider);
		string? providerName = string.IsNullOrWhiteSpace(provider.Value) ? null : provider.Value;

		// Which leafs are secret is the provider's business where it describes them, plus the
		// blanket Password rule that also covers providers describing nothing.
		HashSet<string> secrets = new(StringComparer.OrdinalIgnoreCase);
		if (providerName is not null && Resolve(registry, providerName, role, out _) is { } resolved)
			foreach (BackendConfigField field in resolved.DescribeConfiguration(role))
				if (field.Type == BackendFieldType.Secret)
					secrets.Add(field.Name);

		return new RoleDto(
			role.ToString(), providerName, provider.Source ?? "default", providerName is not null,
			[
				.. leafs
					.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
					.Select(pair => new SettingDto(
						pair.Key,
						IsSecret(pair.Key, secrets) && pair.Value.Value is not null ? SecretMask : pair.Value.Value,
						pair.Value.Source,
						IsSecret(pair.Key, secrets)))
			]);
	}

	private static bool IsSecret(string leaf, HashSet<string> declared)
	{
		return declared.Contains(BackendConfigValidation.ListRoot(leaf)) ||
		       leaf.EndsWith("Password", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	///   The role's leafs as the FILE and environment supply them. The database settings layer
	///   is part of <see cref="IConfiguration" /> too, so it has to be skipped explicitly —
	///   otherwise a stored value would look like a config default and saving it again would
	///   delete the very row being saved.
	/// </summary>
	private static IEnumerable<KeyValuePair<string, string?>> ConfigLeafs(BackendRole role, IConfiguration config)
	{
		string prefix = $"{SectionPrefix}:{role}";
		foreach (KeyValuePair<string, string?> pair in config.GetSection(prefix).AsEnumerable(true))
		{
			if (string.IsNullOrEmpty(pair.Key))
				continue;
			if (FileValue(config, $"{prefix}:{pair.Key}") is { } value)
				yield return new KeyValuePair<string, string?>(pair.Key, value);
		}
	}

	/// <summary>The value the configuration would have without the database layer.</summary>
	private static string? FileValue(IConfiguration config, string fullKey)
	{
		if (config is not IConfigurationRoot root)
			return config[fullKey];
		foreach (IConfigurationProvider provider in root.Providers.Reverse())
		{
			if (provider is DbSettingsConfigurationProvider)
				continue;
			if (provider.TryGet(fullKey, out string? value))
				return value;
		}

		return null;
	}

	private static IEnumerable<KeyValuePair<string, string?>> DbLeafs(
		BackendRole role, Dictionary<string, string?> db)
	{
		string prefix = $"{SectionPrefix}:{role}:";
		return db
			.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.Select(pair => new KeyValuePair<string, string?>(pair.Key[prefix.Length..], pair.Value));
	}

	private static FieldDto Describe(BackendConfigField field)
	{
		return new FieldDto(
			field.Name, field.Label, field.Type.ToString(), field.Required, field.Default,
			field.EnumValues, field.Help, field.Min, field.Max);
	}
}
