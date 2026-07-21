using System.Reflection;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;

namespace ActiveSync.Server;

/// <summary>
///   Emits a human-readable summary of the effective configuration at startup: one line per
///   backend role (described by the role's provider, which redacts its own secrets), the
///   database in use, whether the gateway is read-only, and the EAS tuning knobs. Secrets
///   (passwords in a Postgres connection string) are redacted; backend user credentials
///   never live in config — they arrive per request via Basic auth.
/// </summary>
public static class StartupSummary
{
	/// <param name="logger">Sink for the banner lines.</param>
	/// <param name="options">Bound configuration.</param>
	/// <param name="roles">The global role assignments; null omits the backend lines.</param>
	/// <param name="registry">Provider registry describing each role; null omits the backend lines.</param>
	/// <param name="mergedUsers">
	///   The merged config ⊕ database user view (<see cref="AccountResolver.MergedUsers" />);
	///   null falls back to config-only (hosts without a reachable database).
	/// </param>
	/// <param name="httpsSummary">
	///   State of the gateway's own HTTPS endpoint (self-signed fingerprint / config endpoint
	///   / off), built by the server host; null omits the line (CLI banner — the certificate
	///   may not exist before the first serve).
	/// </param>
	public static void Log(
		ILogger logger, ActiveSyncOptions options,
		BackendRolesConfig? roles = null,
		BackendProviderRegistry? registry = null,
		IReadOnlyDictionary<string, MergedAccount>? mergedUsers = null,
		string? httpsSummary = null)
	{
		string version = Assembly.GetExecutingAssembly()
			                 .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		                 ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
		                 ?? "unknown";

		IReadOnlyDictionary<string, MergedAccount> users = mergedUsers
			?? options.Users?.ToDictionary(
				kv => kv.Key, kv => new MergedAccount(kv.Value, false, false), StringComparer.OrdinalIgnoreCase)
			?? new Dictionary<string, MergedAccount>();

		logger.LogInformation("========================================================");
		logger.LogInformation("ActiveSync gateway v{Version} — EAS 16.1 → mail/DAV backends", version);
		logger.LogInformation(
			options.ReadOnly
				? "Mode:     READ-ONLY (all client writes are suppressed/reverted)"
				: "Mode:     read-write");
		if (users.Count > 0)
		{
			int fromDb = users.Values.Count(u => u.FromDatabase);
			logger.LogInformation(
				"Auth:     backend pass-through + {Count} user override entr{Plural} " +
				"({ConfigCount} config, {DbCount} database){Restricted}",
				users.Count, users.Count == 1 ? "y" : "ies", users.Count - fromDb, fromDb,
				options.RequireDeclaredUsers ? " (declared users only)" : "");
			foreach ((string login, MergedAccount account) in
			         users.OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase))
				logger.LogInformation("User:     {Login}  {Details}", login, DescribeUser(account));
			int plaintextCount = users.Values.Count(u =>
				!string.IsNullOrWhiteSpace(u.Options.Password) && !GatewayPasswordHasher.IsHashed(u.Options.Password));
			if (plaintextCount > 0)
				logger.LogWarning(
					"Auth:     {Count} user(s) have PLAINTEXT gateway passwords — generate hashes with 'hash-password'",
					plaintextCount);
		}
		else
		{
			logger.LogInformation("Auth:     backend pass-through (EAS credentials forwarded to backends)");
			if (options.RequireDeclaredUsers)
				logger.LogWarning(
					"Auth:     RequireDeclaredUsers is ON but no users are declared (config or database) — " +
					"every login will be rejected. Declare users or run 'eas user add'.");
		}

		if (roles is not null && registry is not null)
			foreach ((BackendRole role, RoleAssignment assignment) in roles.Assignments.OrderBy(a => a.Key))
				logger.LogInformation("{Role}: {Description}",
					$"{role}".PadRight(8), DescribeAssignment(registry, role, assignment));

		string databaseProvider = PostgresConnectionUri.EffectiveProvider(options.Database);
		logger.LogInformation("Database: {Provider} — {ConnectionString}",
			databaseProvider, Redact(databaseProvider, options.Database.ConnectionString));
		if (string.IsNullOrWhiteSpace(options.Encryption.Key) && string.IsNullOrWhiteSpace(options.Encryption.KeyFile))
		{
			logger.LogWarning(
				"Storage:  LOCAL CONTENT STORED IN PLAINTEXT (ActiveSync:Encryption:AllowPlaintext) — dev/test only");
		}
		else
		{
			logger.LogInformation("Storage:  local content encrypted at rest (AES-256-GCM)");
			if (EncryptionKeyLoader.IsShortPassphrase(options.Encryption))
				logger.LogWarning(
					"Storage:  encryption key is a SHORT passphrase (< {Length} chars) — consider a longer one " +
					"or 'openssl rand -base64 32'", EncryptionKeyLoader.ShortPassphraseLength);
		}

		logger.LogInformation(
			"EAS:      heartbeat {MinHeartbeat}-{MaxHeartbeat}s, window {DefaultWindow}/{MaxWindow}, " +
			"DAV poll {DavPoll}s, watchdog {Watchdog}, session idle {SessionIdleMin}min, IMAP IDLE {Idle}",
			options.Eas.MinHeartbeatSeconds, options.Eas.MaxHeartbeatSeconds,
			options.Eas.DefaultWindowSize, options.Eas.MaxWindowSize,
			options.Eas.DavPollSeconds,
			options.Eas.WatchdogSeconds > 0 ? $"{options.Eas.WatchdogSeconds}s" : "off",
			options.Eas.SessionIdleMinutes,
			options.Eas.UseImapIdle ? "on" : "off");
		logger.LogInformation(
			"Auth:     throttle {Throttle}, auth cache {SuccessCache}, negative cache {NegativeCache}",
			options.Auth.MaxFailures > 0
				? $"{options.Auth.MaxFailures} failures/{options.Auth.FailureWindowSeconds}s per address"
				: "OFF",
			options.Auth.SuccessCacheMinutes > 0 ? $"{options.Auth.SuccessCacheMinutes}min" : "off",
			options.Auth.NegativeCacheSeconds > 0 ? $"{options.Auth.NegativeCacheSeconds}s" : "off");
		if (httpsSummary is not null)
			logger.LogInformation("HTTPS:    {State}", httpsSummary);
		logger.LogInformation("WebUI:    admin {Admin}, user portal {Portal}{Oidc}",
			options.WebUi.Admin.Enabled ? "/admin" : "off",
			options.WebUi.UserPortal.Enabled ? "/user" : "off",
			options.WebUi.Oidc?.Authority is { Length: > 0 } authority ? $", OIDC {authority}" : "");
		if (!string.IsNullOrWhiteSpace(options.PublicUrl))
			logger.LogInformation("Public:   {PublicUrl} (advertised by Autodiscover)", options.PublicUrl);
		logger.LogInformation("========================================================");
	}

	private static string DescribeAssignment(
		BackendProviderRegistry registry, BackendRole role, RoleAssignment assignment)
	{
		try
		{
			return registry.GetFor(assignment.ProviderName, role).DescribeRole(role, assignment.Settings);
		}
		catch (InvalidOperationException ex)
		{
			return $"{assignment.ProviderName} — INVALID: {ex.Message}";
		}
	}

	/// <summary>
	///   One-line, full-detail description of a declared user — origin, mail address and every
	///   overridden role. Passwords never render; only a masked marker with their format.
	/// </summary>
	internal static string DescribeUser(MergedAccount account)
	{
		AccountOptions o = account.Options;
		List<string> parts =
		[
			account.FromDatabase
				? o.AutoProvisioned == true ? "[db, auto-provisioned]"
				: account.ShadowsConfig ? "[db, shadows config]" : "[db]"
				: "[config]",
		];
		if (o.Enabled == false)
			parts.Add("DISABLED");
		if (!string.IsNullOrWhiteSpace(o.MailAddress))
			parts.Add($"mail={o.MailAddress}");
		if (!string.IsNullOrWhiteSpace(o.Password))
			parts.Add(GatewayPasswordHasher.IsHashed(o.Password)
				? "password=***(pbkdf2)"
				: "password=***(PLAINTEXT)");
		if (o.Admin == true)
			parts.Add("admin");
		foreach ((string roleName, BackendRoleOverride roleOverride) in
		         (o.Backends ?? []).OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
			parts.Add($"{roleName.ToLowerInvariant()}[{DescribeRoleOverride(roleOverride)}]");
		if (parts.Count == 1)
			parts.Add("(allowlist grant — pure pass-through)");
		return string.Join("  ", parts);
	}

	private static string DescribeRoleOverride(BackendRoleOverride roleOverride)
	{
		if (roleOverride.Enabled == false)
			return "off";
		List<string> fields = [];
		if (roleOverride.Provider is not null)
			fields.Add($"provider={roleOverride.Provider}");
		if (roleOverride.UserName is not null)
			fields.Add($"user={roleOverride.UserName}");
		if (roleOverride.Password is not null)
			fields.Add(SecretValue.IsSealed(roleOverride.Password) ? "pw=***(sealed)" : "pw=***(PLAINTEXT)");
		foreach ((string key, string? value) in
		         (roleOverride.Settings ?? []).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
			if (value is not null)
				fields.Add($"{key}={SecretRedaction.MaskIfSecret(key, value)}");
		return string.Join(" ", fields);
	}

	/// <summary>
	///   Redacts the password in a database connection string for the banner. Delegates to the
	///   shared <see cref="SecretRedaction.RedactConnectionString" /> so the banner, the settings
	///   surfaces and any other caller mask identically — including SQLite/SQLCipher strings that
	///   carry a Password keyword, which this used to wave through as "just a file path" (E23).
	///   The provider is kept for the caller's log message; the redaction is content-driven.
	/// </summary>
	internal static string Redact(string provider, string connectionString)
	{
		_ = provider;
		return SecretRedaction.RedactConnectionString(connectionString);
	}
}
