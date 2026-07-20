using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;

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
	///   <paramref name="effective" /> resolves a full configuration key to the value that is in
	///   force — database over file — since the role's provider may well be a stored override.
	/// </summary>
	internal static string? Validate(
		BackendProviderRegistry registry, Func<string, string?> effective, string key, string value)
	{
		string[] parts = key.Split(':');
		if (parts.Length < 4 ||
		    !parts[0].Equals("ActiveSync", StringComparison.OrdinalIgnoreCase) ||
		    !parts[1].Equals("Backends", StringComparison.OrdinalIgnoreCase) ||
		    !Enum.TryParse(parts[2], true, out BackendRole role))
			return null;

		string leaf = string.Join(':', parts[3..]);
		if (leaf.Equals(BackendRolesConfig.ProviderKey, StringComparison.OrdinalIgnoreCase))
			return ProviderError(registry, value, role);

		// Which provider's shape applies: whichever one currently serves the role.
		string? providerName = effective($"ActiveSync:Backends:{role}:{BackendRolesConfig.ProviderKey}");
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

	private static string? ProviderError(BackendProviderRegistry registry, string value, BackendRole role)
	{
		try
		{
			registry.GetFor(value, role);
			return null;
		}
		catch (InvalidOperationException ex)
		{
			return ex.Message;
		}
	}
}
