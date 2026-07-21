using System.Globalization;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The catalogue of settable global configuration keys: full configuration path → type,
///   value bounds, apply tier (live vs restart) and one-line help. It is the single source of
///   truth for `eas config set/unset/list` AND the web admin settings editor — both surfaces
///   validate and enumerate from here. Backend role settings
///   (<c>ActiveSync:Backends:&lt;Role&gt;:*</c>) are open-ended per provider, so they are matched
///   dynamically rather than listed. Host-controlled keys — the two bootstrap sections
///   (Database, Encryption) plus the startup paths <c>UsersFile</c> and <c>Plugins:*</c> — are
///   explicitly NOT settable; see <see cref="HostControlledReason" /> for why.
/// </summary>
internal static class SettingKeys
{
	internal enum ValueType { String, Bool, Int, Number, Enum }

	internal sealed record SettingKey(
		string Key,
		ValueType Type,
		bool Restart,
		string? Default,
		string Help,
		string[]? EnumValues = null,
		long? Min = null,
		long? Max = null,
		bool Secret = false)
	{
		public string Tier => Restart ? "restart" : "live";
	}

	private const string Live = "live";

	/// <summary>The statically-known settable keys (everything except open-ended backend settings).</summary>
	internal static IReadOnlyList<SettingKey> All { get; } =
	[
		new("ActiveSync:ReadOnly", ValueType.Bool, false, "false",
			"Pure-mirror mode: silently revert all client writes."),
		new("ActiveSync:RequireDeclaredUsers", ValueType.Bool, false, "false",
			"Only logins declared in config/database may authenticate."),
		new("ActiveSync:AutoProvisionUsers", ValueType.Bool, false, "true",
			"Create a database account for a pass-through login on its first successful EAS sign-in (on by default)."),
		new("ActiveSync:PublicUrl", ValueType.String, false, null,
			"Public base URL advertised by Autodiscover (else derived from the request)."),
		// ActiveSync:UsersFile and ActiveSync:Plugins:Directory are deliberately ABSENT — see
		// HostControlledReason. They name things the host reads off disk at startup (one of them
		// assemblies it then executes), so they are file/env only.

		new("ActiveSync:Eas:MinHeartbeatSeconds", ValueType.Int, false, "60",
			"Minimum Ping/Sync heartbeat.", Min: 1, Max: 3540),
		new("ActiveSync:Eas:MaxHeartbeatSeconds", ValueType.Int, false, "1770",
			"Maximum Ping/Sync heartbeat (EAS cap 3540).", Min: 1, Max: 3540),
		new("ActiveSync:Eas:DavPollSeconds", ValueType.Int, false, "60",
			"DAV collection poll interval during Ping.", Min: 1, Max: 86400),
		new("ActiveSync:Eas:MaxWindowSize", ValueType.Int, false, "512",
			"Maximum Sync window size.", Min: 1, Max: 522),
		new("ActiveSync:Eas:DefaultWindowSize", ValueType.Int, false, "100",
			"Default Sync window size.", Min: 1, Max: 522),
		new("ActiveSync:Eas:SessionIdleMinutes", ValueType.Int, false, "15",
			"Idle backend session eviction time.", Min: 1, Max: 1440),
		new("ActiveSync:Eas:UseImapIdle", ValueType.Bool, false, "true",
			"Use a dedicated IMAP IDLE connection for push."),
		new("ActiveSync:Eas:WatchdogSeconds", ValueType.Int, false, "60",
			"Pending-change re-check interval (0 disables; otherwise >= 15).", Min: 0, Max: 86400),

		new("ActiveSync:Auth:MaxFailures", ValueType.Int, false, "20",
			"Failed auths per client before 429 (0 disables).", Min: 0, Max: 1000000),
		new("ActiveSync:Auth:FailureWindowSeconds", ValueType.Int, false, "300",
			"Failure-count window.", Min: 1, Max: 86400),
		new("ActiveSync:Auth:NegativeCacheSeconds", ValueType.Int, false, "15",
			"Bad-credential cache TTL (0 disables).", Min: 0, Max: 86400),
		new("ActiveSync:Auth:SuccessCacheMinutes", ValueType.Int, false, "5",
			"Good-credential cache TTL (0 disables).", Min: 0, Max: 1440),
		new("ActiveSync:Auth:UsersRefreshSeconds", ValueType.Number, false, "1",
			"Database change-stamp poll cadence (0 = every request; negative = load once)."),

		new("ActiveSync:Log:Mode", ValueType.Enum, true, "Standard",
			"Console line shape.", EnumValues: ["Simple", "Standard", "Extended"]),
		new("ActiveSync:Log:Format", ValueType.Enum, true, "Text",
			"Console output format.", EnumValues: ["Text", "Json"]),
		new("ActiveSync:Log:Database", ValueType.Bool, false, "true",
			"Persist logs to the state database (for 'eas logs' and the web admin logs view)."),
		new("ActiveSync:Log:DbMinimumLevel", ValueType.Enum, false, "Information",
			"Minimum level persisted to the database.",
			EnumValues: ["Information", "Warning", "Error", "Fatal"]),
		new("ActiveSync:Log:RetentionDays", ValueType.Int, false, "7",
			"Days of database log history to keep (0 disables the sweep).", Min: 0, Max: 3650),

		new("ActiveSync:Policy:Enabled", ValueType.Bool, false, "false",
			"Master switch for device policy enforcement."),
		new("ActiveSync:Policy:DevicePasswordEnabled", ValueType.Bool, false, "false",
			"Require a device lock PIN/password."),
		new("ActiveSync:Policy:AlphanumericDevicePasswordRequired", ValueType.Bool, false, "false",
			"Require letters plus digits/symbols."),
		new("ActiveSync:Policy:AllowSimpleDevicePassword", ValueType.Bool, false, "true",
			"Allow simple PINs like 1234."),
		new("ActiveSync:Policy:MinDevicePasswordLength", ValueType.Int, false, null,
			"Minimum device password length.", Min: 1, Max: 16),
		new("ActiveSync:Policy:MinDevicePasswordComplexCharacters", ValueType.Int, false, null,
			"Minimum character classes.", Min: 1, Max: 4),
		new("ActiveSync:Policy:MaxInactivityTimeDeviceLock", ValueType.Int, false, null,
			"Inactivity lock seconds.", Min: 1, Max: 9999),
		new("ActiveSync:Policy:MaxDevicePasswordFailedAttempts", ValueType.Int, false, null,
			"Wrong tries before local wipe.", Min: 4, Max: 16),
		new("ActiveSync:Policy:DevicePasswordExpiration", ValueType.Int, false, null,
			"Password expiry days (0 = never).", Min: 0, Max: 1000),
		new("ActiveSync:Policy:DevicePasswordHistory", ValueType.Int, false, null,
			"Prohibited password-reuse count.", Min: 0, Max: 1000),
		new("ActiveSync:Policy:RequireDeviceEncryption", ValueType.Bool, false, "false",
			"Require device storage encryption."),
		new("ActiveSync:Policy:MaxAttachmentSize", ValueType.Int, false, null,
			"Maximum attachment download bytes.", Min: 0, Max: 2147483647),
		new("ActiveSync:Policy:PasswordRecoveryEnabled", ValueType.Bool, false, "false",
			"Allow device recovery-password escrow."),

		new("ActiveSync:Metrics:Enabled", ValueType.Bool, true, "false",
			"Enable the Prometheus metrics exporter."),
		new("ActiveSync:Metrics:Port", ValueType.Int, true, null,
			"Dedicated /metrics port (unset = shares main listeners).", Min: 1, Max: 65535),
		new("ActiveSync:Metrics:PerUser", ValueType.Bool, false, "true",
			"Per-account (user=) metric labels."),

		new("ActiveSync:Tls:Enabled", ValueType.Bool, true, "true",
			"Serve the gateway's own HTTPS listener (off = terminate TLS in front)."),
		new("ActiveSync:Tls:Port", ValueType.Int, true, "5443",
			"HTTPS listen port.", Min: 1, Max: 65535),
		new("ActiveSync:Tls:CertificatePath", ValueType.String, true, null,
			"Path to a mounted certificate to serve (PEM full chain, or a PFX bundle); unset = self-signed."),
		new("ActiveSync:Tls:CertificateKeyPath", ValueType.String, true, null,
			"Path to the PEM private key that pairs with a PEM CertificatePath (unset for a PFX)."),
		new("ActiveSync:Tls:CertificatePassword", ValueType.String, true, null,
			"Password for the PFX bundle or an encrypted PEM key (stored sealed).", Secret: true),

		new("ActiveSync:WebUi:Admin:Enabled", ValueType.Bool, false, "false",
			"Serve the web admin interface under /admin."),
		new("ActiveSync:WebUi:UserPortal:Enabled", ValueType.Bool, false, "false",
			"Serve the user self-service portal under /user."),
		new("ActiveSync:WebUi:AllowInsecureCookies", ValueType.Bool, true, "false",
			"Emit web session cookies without Secure over plain http (local development only)."),
		new("ActiveSync:WebUi:Oidc:Enabled", ValueType.Bool, true, "true",
			"Master switch for OIDC; false keeps the settings but reverts web login to local passwords."),
		new("ActiveSync:WebUi:Oidc:Authority", ValueType.String, true, null,
			"OIDC issuer URL; when set (and enabled), web logins go through the identity provider (local web login off)."),
		new("ActiveSync:WebUi:Oidc:ClientId", ValueType.String, true, null,
			"OIDC client id of this gateway."),
		new("ActiveSync:WebUi:Oidc:ClientSecret", ValueType.String, true, null,
			"OIDC client secret (plaintext or enc:v1: sealed).", Secret: true),
		new("ActiveSync:WebUi:Oidc:Scopes", ValueType.String, true, "openid profile email",
			"Space-separated OIDC scopes to request."),
		new("ActiveSync:WebUi:Oidc:LoginClaim", ValueType.String, true, "preferred_username",
			"Token claim mapped to the gateway login."),
		new("ActiveSync:WebUi:Oidc:AdminClaim", ValueType.String, false, null,
			"Token claim granting web admin access (alternative to the account Admin flag)."),
		new("ActiveSync:WebUi:Oidc:AdminClaimValue", ValueType.String, false, null,
			"Value of AdminClaim that grants admin; required when AdminClaim is set ('*' = any value)."),
		new("ActiveSync:WebUi:Oidc:AutoProvision", ValueType.Bool, false, "false",
			"Create a database account for unknown OIDC logins on first sign-in."),
		new("ActiveSync:WebUi:Oidc:RequireHttpsMetadata", ValueType.Bool, true, "true",
			"Require HTTPS for the OIDC discovery endpoint (disable only for dev)."),

		new("ActiveSync:Cli:Enabled", ValueType.Bool, false, "true",
			"Answer the loopback CLI-forwarding endpoint (/cli) that the slim 'eas' client uses."),
	];

	private static readonly Dictionary<string, SettingKey> ByKey =
		All.ToDictionary(k => k.Key, StringComparer.OrdinalIgnoreCase);

	/// <summary>Roles whose backend sections accept open-ended provider settings.</summary>
	private static readonly HashSet<string> Roles =
		new(Enum.GetNames<BackendRole>(), StringComparer.OrdinalIgnoreCase);

	/// <summary>
	///   Keys that may only come from the environment or a config file, never the database.
	///   Returns the operator-facing reason, or null for an ordinary settable key.
	///
	///   Two classes. The BOOTSTRAP sections (Database, Encryption) are needed to open and
	///   decrypt the database that stores everything else. The HOST-CONTROLLED paths name
	///   something the host reads off disk before it trusts anything: <c>Plugins:Directory</c> is
	///   scanned for plugin assemblies that are then loaded into this process (with the master key
	///   in memory, and with the live <c>IServiceCollection</c> handed to them), and
	///   <c>UsersFile</c> is read as a non-optional JSON configuration file. Leaving either
	///   database-settable would make settings-write access — the admin UI, `eas config set`, a
	///   DBA, SQL injection anywhere in the system — an arbitrary-code-execution primitive.
	///   Enforced on the way in (the write surfaces below) AND on the way out
	///   (<c>DbSettingsConfigurationProvider.SetData</c> drops such rows), so a row inserted
	///   behind the write path is inert rather than merely unwritable.
	/// </summary>
	internal static string? HostControlledReason(string key)
	{
		if (IsSection(key, "ActiveSync:Database") || IsSection(key, "ActiveSync:Encryption"))
			return "bootstrap settings (Database, Encryption) must come from the environment or a " +
			       "config file — they are needed to open and decrypt the database";

		if (key.Equals("ActiveSync:UsersFile", StringComparison.OrdinalIgnoreCase) ||
		    IsSection(key, "ActiveSync:Plugins"))
			return "host-controlled settings (UsersFile, Plugins) must come from the environment or a " +
			       "config file — they name files the gateway reads, and code it loads, at startup";

		return null;
	}

	/// <summary>True for the section root itself and anything beneath it.</summary>
	private static bool IsSection(string key, string section) =>
		key.Equals(section, StringComparison.OrdinalIgnoreCase) ||
		key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	///   Resolves a key to its definition. Statically-known keys return their catalogue entry;
	///   <c>ActiveSync:Backends:&lt;Role&gt;:*</c> returns a synthetic string/live entry (the role's
	///   provider validates the specifics on the server). Returns null for anything else.
	/// </summary>
	internal static SettingKey? Find(string key)
	{
		if (ByKey.TryGetValue(key, out SettingKey? known))
			return known;

		string[] parts = key.Split(':');
		if (parts.Length >= 4 &&
		    parts[0].Equals("ActiveSync", StringComparison.OrdinalIgnoreCase) &&
		    parts[1].Equals("Backends", StringComparison.OrdinalIgnoreCase) &&
		    Roles.Contains(parts[2]))
			return new SettingKey(key, ValueType.String, false, null,
				$"Backend setting for the {parts[2]} role (validated by its provider).",
				Secret: parts[^1].Equals("Password", StringComparison.OrdinalIgnoreCase));

		return null;
	}

	/// <summary>Validates a value against a key's type/bounds. Returns an error message or null.</summary>
	internal static string? Validate(SettingKey key, string value)
	{
		switch (key.Type)
		{
			case ValueType.String:
				return null;
			case ValueType.Bool:
				return bool.TryParse(value, out _) ? null : $"'{value}' is not a boolean (true/false).";
			case ValueType.Enum:
				return key.EnumValues!.Contains(value, StringComparer.OrdinalIgnoreCase)
					? null
					: $"'{value}' is not one of: {string.Join(", ", key.EnumValues!)}.";
			case ValueType.Number:
				return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
					? null
					: $"'{value}' is not a number.";
			case ValueType.Int:
				if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
					return $"'{value}' is not an integer.";
				if (key.Min is { } min && parsed < min)
					return $"{parsed} is below the minimum {min} for {key.Key}.";
				if (key.Max is { } max && parsed > max)
					return $"{parsed} is above the maximum {max} for {key.Key}.";
				return null;
			default:
				return null;
		}
	}
}
