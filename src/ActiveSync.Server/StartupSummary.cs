using System.Reflection;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Crypto;

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
	///   The merged config ⊕ database user view (<see cref="UserResolver.MergedUsers" />);
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
		IReadOnlyDictionary<string, MergedUser>? mergedUsers = null,
		string? httpsSummary = null)
	{
		string version = Assembly.GetExecutingAssembly()
			                 .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		                 ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
		                 ?? "unknown";

		// Read the holder from the assembly attribute (stamped by <Copyright> in
		// Directory.Build.props) rather than repeating it here, so the two cannot drift.
		string copyright = Assembly.GetExecutingAssembly()
			.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

		IReadOnlyDictionary<string, MergedUser> users = mergedUsers
			?? options.Users?.ToDictionary(
				kv => kv.Key, kv => new MergedUser(kv.Value, false, false), StringComparer.OrdinalIgnoreCase)
			?? new Dictionary<string, MergedUser>();

		logger.LogInformation("========================================================");
		logger.LogInformation("ActiveSync gateway v{Version} — EAS 16.1 → mail/DAV backends", version);
		// A visible licence notice is what stops an operator claiming they never saw the terms,
		// which matters more for a noncommercial licence than for a permissive one. Both
		// surfaces reach this method: the serve banner and bare `eas` (BannerCommand →
		// CliVerbs.ShowBannerAsync).
		logger.LogInformation(
			"Licence:  PolyForm Noncommercial 1.0.0 — noncommercial use only (see LICENSE){Copyright}",
			copyright.Length > 0 ? $" — {copyright}" : "");
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
				options.AutoProvisionUsers ? "" : " (declared users only — AutoProvisionUsers off)");
			foreach ((string login, MergedUser account) in
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
			if (!options.AutoProvisionUsers)
				logger.LogWarning(
					"Auth:     AutoProvisionUsers is OFF but no users are declared (config or database) — " +
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
	///   One-line, full-detail description of a user — origin, mail address and every overridden
	///   role. Passwords never render; only a masked marker with their format.
	///   <para>
	///     Values are the PER-FIELD merge of the database declaration over the configuration one,
	///     so each field is tagged with the level it actually came from — <c>{db}</c> or
	///     <c>{cfg}</c> — whenever both levels are in play. Without that, a merged line reads as
	///     if one source produced all of it, which is exactly the question an operator has when
	///     a user is declared in two places.
	///   </para>
	/// </summary>
	internal static string DescribeUser(MergedUser account)
	{
		UserOptions o = account.Options;
		List<string> parts =
		[
			account.FromDatabase
				? o.AutoProvisioned == true ? "[db, auto-provisioned]"
				: account.ShadowsConfig ? "[db+config, merged per field]" : "[db]"
				: "[config]",
		];
		// Only worth the noise when the two levels can actually disagree.
		bool showSources = account.ShadowsConfig;
		string Tag(string path) => showSources
			? account.SourceOf(path) switch
			{
				UserFieldSource.UserDatabase => "{db}",
				UserFieldSource.UserConfig => "{cfg}",
				_ => "",
			}
			: "";

		if (account.Invalid)
			parts.Add("INVALID (refused)");
		if (o.Enabled == false)
			parts.Add($"DISABLED{Tag("Enabled")}");
		if (!string.IsNullOrWhiteSpace(o.MailAddress))
			parts.Add($"mail={o.MailAddress}{Tag("MailAddress")}");
		if (!string.IsNullOrWhiteSpace(o.Password))
			parts.Add((GatewayPasswordHasher.IsHashed(o.Password)
				? "password=***(pbkdf2)"
				: "password=***(PLAINTEXT)") + Tag("Password"));
		if (!string.IsNullOrWhiteSpace(o.DefaultBackendLogin))
			parts.Add($"backend-user={o.DefaultBackendLogin}{Tag("DefaultBackendLogin")}");
		if (!string.IsNullOrWhiteSpace(o.DefaultBackendPassword))
			parts.Add((SecretValue.IsSealed(o.DefaultBackendPassword)
				? "backend-pw=***(sealed)"
				: "backend-pw=***(PLAINTEXT)") + Tag("DefaultBackendPassword"));
		if (o.Admin == true)
			parts.Add($"admin{Tag("Admin")}");
		foreach ((string roleName, BackendRoleOverride roleOverride) in
		         (o.Backends ?? []).OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
			parts.Add($"{roleName.ToLowerInvariant()}[{DescribeRoleOverride(roleOverride, roleName, Tag)}]");
		if (parts.Count == 1)
			parts.Add("(allowlist grant — pure pass-through)");
		return string.Join("  ", parts);
	}

	private static string DescribeRoleOverride(
		BackendRoleOverride roleOverride, string roleName, Func<string, string> tag)
	{
		if (roleOverride.Enabled == false)
			return "off" + tag($"Backends:{roleName}:Enabled");
		List<string> fields = [];
		if (roleOverride.Provider is not null)
			fields.Add($"provider={roleOverride.Provider}{tag($"Backends:{roleName}:Provider")}");
		if (roleOverride.UserName is not null)
			fields.Add($"user={roleOverride.UserName}{tag($"Backends:{roleName}:UserName")}");
		if (roleOverride.Password is not null)
			fields.Add((SecretValue.IsSealed(roleOverride.Password) ? "pw=***(sealed)" : "pw=***(PLAINTEXT)")
				+ tag($"Backends:{roleName}:Password"));
		foreach ((string key, string? value) in
		         (roleOverride.Settings ?? []).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
			fields.Add(value is null
				// A null Settings value is the explicit-clear directive, not an absent key.
				? $"{key}=(cleared){tag($"Backends:{roleName}:Settings:{key}")}"
				: $"{key}={SecretRedaction.MaskIfSecret(key, value)}{tag($"Backends:{roleName}:Settings:{key}")}");
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
