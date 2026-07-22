using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
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

		string leaf = string.Join(':', parts[3..]);
		if (leaf.Equals(BackendRolesConfig.ProviderKey, StringComparison.OrdinalIgnoreCase))
			return ProviderError(registry, effective, role, value);

		// Which provider's shape applies: whichever one currently serves the role.
		string? providerName = effective[$"ActiveSync:Backends:{role}:{BackendRolesConfig.ProviderKey}"];
		if (string.IsNullOrWhiteSpace(providerName))
			return null;

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
		if (field is null || field.Type == BackendFieldType.StringList)
			return null;

		return BackendConfigValidation.CheckValue(field, value)?.Message;
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

		// B24: switching a role's Provider is not just "can this provider serve the role" — the
		// settings ALREADY stored under the role must satisfy the NEW provider's schema too. Otherwise
		// `eas config set ...:Calendar:Provider carddav` is accepted over a caldav-shaped section and
		// only surfaces at the next restart (the live rebuild doesn't validate it — B14). Re-validate
		// the effective section against the incoming provider.
		Dictionary<string, string?> effectiveSettings = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string leaf, string? leafValue) in
		         effective.GetSection($"ActiveSync:Backends:{role}").AsEnumerable(makePathsRelative: true))
			if (!string.IsNullOrEmpty(leaf) &&
			    !leaf.Equals(BackendRolesConfig.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
			    leafValue is not null)
				effectiveSettings[leaf] = leafValue;

		IReadOnlyList<BackendFieldError> errors = BackendConfigValidation.Validate(provider, role, effectiveSettings);
		return errors.Count > 0 ? errors[0].Message : null;
	}
}
