using System.Security.Cryptography;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Crypto;
using Microsoft.Extensions.Configuration;

namespace ActiveSync.Core.Administration;

/// <summary>
///   Validation for the open-ended <c>ActiveSync:Backends:&lt;Role&gt;:*</c> keys, which the static
///   <see cref="SettingKeys" /> catalogue can only describe as strings. The provider assigned to
///   the role knows better: it names a role it can actually serve, and describes the shape of
///   each setting it reads. Keys no provider describes still pass — plugin providers may
///   describe part of their surface, or none of it.
/// </summary>
internal static class BackendKeyValidator
{
	/// <summary>
	///   An error message, or null when the value is acceptable (or not ours to judge).
	///   <paramref name="effective" /> is the configuration in force — database over file — since the
	///   role's provider and its settings may well be stored overrides.
	/// </summary>
	internal static string? Validate(
		BackendProviderRegistry registry, IConfiguration effective, string key, string value)
	{
		string[] parts = key.Split(':');
		if (parts.Length < 4 ||
		    !parts[0].Equals("ActiveSync", StringComparison.OrdinalIgnoreCase) ||
		    !parts[1].Equals("Backends", StringComparison.OrdinalIgnoreCase) ||
		    !Enum.TryParse(parts[2], true, out BackendRole role))
			return null;

		string? shapeError = ValidateShape(registry, effective, role, parts, value);
		if (shapeError is not null)
			return shapeError;

		// B5: the shape check above only judges the leaf/provider in isolation — it does not know
		// that a config-declared user's OWN settings (BackendRoleOverride.Settings, merged onto
		// whichever provider a role currently names) can become invalid under a NEW provider even
		// though nothing about the write itself looks wrong. UserResolver.ValidateUsers is the
		// OTHER thing BackendConfigurationValidator.Validate runs at startup — simulate the write
		// and surface only failures it NEWLY introduces (same before/after diff shape as
		// SettingKeys.ValidateStartupImpact).
		return ValidateAgainstDeclaredUsers(registry, effective, key, value);
	}

	private static string? ValidateShape(
		BackendProviderRegistry registry, IConfiguration effective, BackendRole role, string[] parts, string value)
	{
		string leaf = string.Join(':', parts[3..]);
		if (leaf.Equals(BackendRolesConfig.ProviderKey, StringComparison.OrdinalIgnoreCase))
			return ProviderError(registry, effective, role, value);

		// Which provider's shape applies: whichever one currently serves the role.
		string? providerName = effective[$"ActiveSync:Backends:{role}:{BackendRolesConfig.ProviderKey}"];
		if (string.IsNullOrWhiteSpace(providerName))
			return InertCredentialLeaf(role, leaf);

		IBackendProvider provider;
		try
		{
			provider = registry.GetFor(providerName, role);
		}
		catch (InvalidOperationException)
		{
			return null; // an unusable assignment already reports itself elsewhere
		}

		BackendConfigField? field = provider.DescribeConfiguration(role)
			.FirstOrDefault(f => f.Name.Equals(
				BackendConfigValidation.ListRoot(leaf), StringComparison.OrdinalIgnoreCase));
		if (field is null)
			return InertCredentialLeaf(role, leaf);
		if (field.Type == BackendFieldType.StringList)
			return null;

		return BackendConfigValidation.CheckValue(field, value)?.Message;
	}

	/// <summary>
	///   Simulates the pending write against every config-declared user and reports only the
	///   failures it introduces — a gateway already sitting on an unrelated invalid user entry must
	///   not have every unrelated write blocked by it.
	/// </summary>
	private static string? ValidateAgainstDeclaredUsers(
		BackendProviderRegistry registry, IConfiguration effective, string key, string value)
	{
		ActiveSyncOptions options = Bind(effective);
		if (options.Users is not { Count: > 0 })
			return null; // nothing declared for this write to invalidate

		byte[]? encryptionKey = EncryptionKeyLoader.TryLoadKey(options.Encryption, out _);
		try
		{
			List<string> before = new();
			UserResolver.ValidateUsers(options, BackendRolesConfig.Load(effective, new List<string>()),
				registry, encryptionKey, before);

			IConfiguration candidate = new ConfigurationBuilder()
				.AddConfiguration(effective)
				.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
				.Build();
			ActiveSyncOptions candidateOptions = Bind(candidate);
			List<string> after = new();
			UserResolver.ValidateUsers(candidateOptions, BackendRolesConfig.Load(candidate, new List<string>()),
				registry, encryptionKey, after);

			List<string> introduced = after.Where(failure => !before.Contains(failure)).ToList();
			return introduced.Count > 0 ? string.Join(" ", introduced) : null;
		}
		finally
		{
			if (encryptionKey is not null)
				CryptographicOperations.ZeroMemory(encryptionKey);
		}
	}

	private static ActiveSyncOptions Bind(IConfiguration config) =>
		config.GetSection("ActiveSync").Get<ActiveSyncOptions>() ?? new ActiveSyncOptions();

	/// <summary>
	///   Backend credentials never come from settings — they are RESOLVED per user (the role
	///   override, then the user default, then the presented EAS credential) and handed to the
	///   provider as <see cref="BackendCredentials" />. A global
	///   <c>ActiveSync:Backends:&lt;Role&gt;:Password</c> was nonetheless accepted, stored, and even
	///   masked as a secret while being read by nothing at all: it looked exactly like configuring
	///   one shared mail password for everyone, and silently was not. Refuse it and name the thing
	///   that does work.
	///   <para>
	///     Only when NO provider field claims the leaf. A plugin that genuinely describes a
	///     "Password" setting of its own still owns that name — it is checked against the schema
	///     above, like any other declared field.
	///   </para>
	/// </summary>
	private static string? InertCredentialLeaf(BackendRole role, string leaf) =>
		leaf.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
		leaf.Equals("UserName", StringComparison.OrdinalIgnoreCase)
			? $"ActiveSync:Backends:{role}:{leaf} is not a setting — no provider reads it, so it would " +
			  "have no effect. Backend credentials are per user: set " +
			  $"Backends:{role}:{leaf} on the user (eas user set <login> Backends:{role}:{leaf} ...), " +
			  (leaf.Equals("Password", StringComparison.OrdinalIgnoreCase)
				  ? "or DefaultBackendPassword to cover every role for that user."
				  : "or DefaultBackendLogin to cover every role for that user.")
			: null;

	/// <summary>
	///   B12: the write surfaces (`eas config set`, the web settings PUT) already refuse a global
	///   Password/UserName leaf via <see cref="InertCredentialLeaf" /> — but that check runs only on
	///   the value being WRITTEN, so the identical key placed directly in a config file (never going
	///   through either write surface) was silently accepted and silently inert. Applied to an
	///   ASSIGNED role's CURRENT settings, so <see cref="BackendConfigurationValidator" /> can catch
	///   it too, from whichever layer (file, env, or database) the value actually arrived through.
	/// </summary>
	internal static IEnumerable<string> InertCredentialLeaves(
		IBackendProvider provider, BackendRole role, ProviderSettings settings)
	{
		foreach (string leaf in new[] { "Password", "UserName" })
		{
			if (string.IsNullOrWhiteSpace(settings.Section[leaf]))
				continue;
			bool claimed = provider.DescribeConfiguration(role)
				.Any(f => f.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
			if (!claimed && InertCredentialLeaf(role, leaf) is { } message)
				yield return message;
		}
	}

	/// <summary>
	///   Whether a backend leaf key holds a secret, for masking in `eas config list/get`. The
	///   provider's own schema is authoritative (B25) — a declared <see cref="BackendFieldType.Secret" />
	///   field, whatever its name — with the <see cref="SecretRedaction.IsSecretName" /> name heuristic
	///   as the fallback when no field claims the leaf (a plugin describing part or none of its surface).
	/// </summary>
	internal static bool IsSecretLeaf(BackendProviderRegistry registry, IConfiguration effective, string key)
	{
		string[] parts = key.Split(':');
		if (parts.Length < 4 ||
		    !parts[0].Equals("ActiveSync", StringComparison.OrdinalIgnoreCase) ||
		    !parts[1].Equals("Backends", StringComparison.OrdinalIgnoreCase) ||
		    !Enum.TryParse(parts[2], true, out BackendRole role))
			return SecretRedaction.IsSecretName(parts[^1]);

		string leaf = string.Join(':', parts[3..]);
		string? providerName = effective[$"ActiveSync:Backends:{role}:{BackendRolesConfig.ProviderKey}"];
		if (!string.IsNullOrWhiteSpace(providerName))
			try
			{
				BackendConfigField? field = registry.GetFor(providerName, role)
					.DescribeConfiguration(role)
					.FirstOrDefault(f => f.Name.Equals(
						BackendConfigValidation.ListRoot(leaf), StringComparison.OrdinalIgnoreCase));
				if (field is not null)
					return field.Type == BackendFieldType.Secret;
			}
			catch (InvalidOperationException)
			{
				// Unusable provider assignment — fall back to the name heuristic below.
			}

		return SecretRedaction.IsSecretName(parts[^1]);
	}

	private static string? ProviderError(
		BackendProviderRegistry registry, IConfiguration effective, BackendRole role, string value)
	{
		IBackendProvider provider;
		try
		{
			provider = registry.GetFor(value, role);
		}
		catch (InvalidOperationException ex)
		{
			return ex.Message;
		}

		// B24: switching a role's Provider is not just "can this provider serve the role" — a value
		// ALREADY stored under the role that is mis-SHAPED for the NEW provider (an out-of-range Port,
		// an unknown enum) must be rejected now, not surface at the next restart (the live rebuild
		// doesn't validate it — B14). Only PRESENT values are checked, per-field, exactly as the
		// non-Provider branch checks a single leaf — completeness (a still-missing required field) is
		// a startup concern the operator is mid-filling, so it must not block assigning the provider.
		IReadOnlyList<BackendConfigField> schema = provider.DescribeConfiguration(role);
		foreach ((string leaf, string? leafValue) in
		         effective.GetSection($"ActiveSync:Backends:{role}").AsEnumerable(makePathsRelative: true))
		{
			if (string.IsNullOrEmpty(leaf) || string.IsNullOrWhiteSpace(leafValue) ||
			    leaf.Equals(BackendRolesConfig.ProviderKey, StringComparison.OrdinalIgnoreCase))
				continue;

			BackendConfigField? field = schema.FirstOrDefault(f =>
				f.Name.Equals(BackendConfigValidation.ListRoot(leaf), StringComparison.OrdinalIgnoreCase));
			if (field is null || field.Type == BackendFieldType.StringList)
				continue;

			if (BackendConfigValidation.CheckValue(field, leafValue.Trim()) is { } error)
				return error.Message;
		}

		return null;
	}
}
