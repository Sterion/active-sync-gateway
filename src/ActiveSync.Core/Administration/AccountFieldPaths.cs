using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Administration;

/// <summary>
///   Config-path access to <see cref="AccountOptions" /> fields, shared by the CLI and the
///   web admin API. Reserved keys are fixed ("MailAddress", "Password", "Admin",
///   "Backends:&lt;Role&gt;:Provider|Enabled|UserName|Password"); provider settings are
///   free-form under "Backends:&lt;Role&gt;:Settings:&lt;Key&gt;" — the host cannot enumerate
///   them (each provider binds its own options), so they are accepted as-is and validated by
///   the role's provider on save.
/// </summary>
internal static class AccountFieldPaths
{
	internal sealed record FieldPath(
		string Key,
		Type ValueType,
		bool IsSecret,
		Action<AccountOptions, object?> Set)
	{
		/// <summary>
		///   True only for the gateway login password (hashed via pbkdf2); false for the per-role
		///   backend passwords (sealed via enc:v1:) and for non-secret fields. An explicit flag so
		///   callers stop inferring "gateway vs backend" from the absence of a ':' in the key (L43).
		/// </summary>
		public bool IsGatewayPassword { get; init; }
	}

	internal static IReadOnlyCollection<string> Keys { get; } =
	[
		"MailAddress", "Password", "Admin", "Enabled", "OidcSubject",
		"Backends:<Role>:Provider", "Backends:<Role>:Enabled",
		"Backends:<Role>:UserName", "Backends:<Role>:Password",
		"Backends:<Role>:Settings:<Key>",
		$"  (roles: {string.Join(", ", Enum.GetNames<BackendRole>())})"
	];

	/// <summary>The per-role backend password keys accepted by `user secret`.</summary>
	internal static IReadOnlyCollection<string> BackendSecretKeys { get; } =
		Enum.GetNames<BackendRole>().Select(role => $"Backends:{role}:Password").ToArray();

	internal static FieldPath? Find(string key)
	{
		if (key.Equals("MailAddress", StringComparison.OrdinalIgnoreCase))
			return new FieldPath("MailAddress", typeof(string), false,
				(account, value) => account.MailAddress = (string?)value);
		if (key.Equals("Password", StringComparison.OrdinalIgnoreCase))
			return new FieldPath("Password", typeof(string), true,
				(account, value) => account.Password = (string?)value) { IsGatewayPassword = true };
		if (key.Equals("Admin", StringComparison.OrdinalIgnoreCase))
			return new FieldPath("Admin", typeof(bool?), false,
				(account, value) => account.Admin = (bool?)value);
		if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
			return new FieldPath("Enabled", typeof(bool?), false,
				(account, value) => account.Enabled = (bool?)value);
		// Settable so an operator can bind a config-declared account to its IdP subject up
		// front, and clear the binding after a genuine identity-provider migration.
		if (key.Equals("OidcSubject", StringComparison.OrdinalIgnoreCase))
			return new FieldPath("OidcSubject", typeof(string), false,
				(account, value) => account.OidcSubject = (string?)value);

		string[] parts = key.Split(':');
		if (parts.Length < 3 ||
		    !parts[0].Equals("Backends", StringComparison.OrdinalIgnoreCase) ||
		    !Enum.TryParse(parts[1], true, out BackendRole role))
			return null;

		string roleName = role.ToString();
		if (parts.Length == 3)
			return parts[2].ToLowerInvariant() switch
			{
				"provider" => RoleField(roleName, "Provider", typeof(string), false,
					(o, v) => o.Provider = (string?)v),
				"enabled" => RoleField(roleName, "Enabled", typeof(bool?), false,
					(o, v) => o.Enabled = (bool?)v),
				"username" => RoleField(roleName, "UserName", typeof(string), false,
					(o, v) => o.UserName = (string?)v),
				"password" => RoleField(roleName, "Password", typeof(string), true,
					(o, v) => o.Password = (string?)v),
				_ => null
			};

		if (!parts[2].Equals("Settings", StringComparison.OrdinalIgnoreCase) || parts.Length < 4)
			return null;
		string settingKey = string.Join(':', parts[3..]);
		return RoleField(roleName, $"Settings:{settingKey}", typeof(string), false, (o, value) =>
		{
			if (value is null)
			{
				o.Settings?.Remove(settingKey);
				if (o.Settings is { Count: 0 })
					o.Settings = null;
			}
			else
			{
				(o.Settings ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
					[settingKey] = (string)value;
			}
		});
	}

	/// <summary>Parses a CLI string into the field's scalar type; error message on failure.</summary>
	internal static bool TryParseValue(FieldPath path, string raw, out object? value, out string? error)
	{
		Type type = Nullable.GetUnderlyingType(path.ValueType) ?? path.ValueType;
		error = null;
		value = null;
		if (type == typeof(string))
		{
			value = raw;
			return true;
		}

		if (type == typeof(bool) && bool.TryParse(raw, out bool parsed))
		{
			value = parsed;
			return true;
		}

		error = $"'{raw}' is not a valid {type.Name} for {path.Key}.";
		return false;
	}

	/// <summary>A field inside one role's override, creating/pruning the override as needed.</summary>
	private static FieldPath RoleField(
		string roleName, string field, Type valueType, bool isSecret,
		Action<BackendRoleOverride, object?> apply)
	{
		return new FieldPath($"Backends:{roleName}:{field}", valueType, isSecret, (account, value) =>
		{
			Dictionary<string, BackendRoleOverride> backends = account.Backends
				??= new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase);
			if (!backends.TryGetValue(roleName, out BackendRoleOverride? roleOverride))
			{
				if (value is null)
					return;
				backends[roleName] = roleOverride = new BackendRoleOverride();
			}

			apply(roleOverride, value);
			// Nulling the last field of a role removes the empty override (and section).
			if (value is null && roleOverride is
			    { Enabled: null, Provider: null, UserName: null, Password: null, Settings: null or { Count: 0 } })
			{
				backends.Remove(roleName);
				if (backends.Count == 0)
					account.Backends = null;
			}
		});
	}
}
