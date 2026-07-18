using System.Reflection;
using System.Text.RegularExpressions;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;

namespace ActiveSync.Server;

/// <summary>
///   Emits a human-readable summary of the effective configuration at startup: which backends
///   are wired up, which database is in use, whether the gateway is read-only, and the EAS
///   tuning knobs. Secrets (passwords in a Postgres connection string) are redacted; backend
///   user credentials never live in config — they arrive per request via Basic auth.
/// </summary>
public static partial class StartupSummary
{
	/// <param name="logger">Sink for the banner lines.</param>
	/// <param name="options">Bound configuration.</param>
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
		logger.LogInformation("ActiveSync gateway v{Version} — EAS 16.1 → IMAP/SMTP/DAV", version);
		logger.LogInformation(
			options.ReadOnly
				? "Mode:     READ-ONLY (all client writes are suppressed/reverted)"
				: "Mode:     read-write");
		if (users.Count > 0)
		{
			int fromDb = users.Values.Count(u => u.FromDatabase);
			logger.LogInformation(
				"Auth:     IMAP pass-through + {Count} user override entr{Plural} " +
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
			logger.LogInformation("Auth:     IMAP pass-through (EAS credentials forwarded to backends)");
			if (options.RequireDeclaredUsers)
				logger.LogWarning(
					"Auth:     RequireDeclaredUsers is ON but no users are declared (config or database) — " +
					"every login will be rejected. Declare users or run 'eas user add'.");
		}

		logger.LogInformation("IMAP:     {Host}:{Port}  ssl={Ssl} security={Security} idle={Idle} certs={Certs}",
			options.Imap.Host, options.Imap.Port, options.Imap.UseSsl,
			options.Imap.Security ?? "(auto)", options.Eas.UseImapIdle ? "on" : "off",
			CertMode(options.Imap.AllowInvalidCertificates, options.Imap.CaCertificatePath));
		logger.LogInformation("SMTP:     {Host}:{Port}  ssl={Ssl} security={Security} certs={Certs}",
			options.Smtp.Host, options.Smtp.Port, options.Smtp.UseSsl,
			options.Smtp.Security ?? "(auto)",
			CertMode(options.Smtp.AllowInvalidCertificates, options.Smtp.CaCertificatePath));

		logger.LogInformation("CalDAV:   {State}", DescribeDav(options.CalDav));
		logger.LogInformation("CardDAV:  {State}", DescribeDav(options.CardDav));
		logger.LogInformation("Tasks:    {State}",
			options.CalDav is not null && !string.IsNullOrWhiteSpace(options.CalDav.TaskFolder)
				? $"CalDAV folder \"{options.CalDav.TaskFolder}\" (when present)"
				: "local storage (gateway database)");
		logger.LogInformation("Notes:    local storage (gateway database)");

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
			"DAV poll {DavPoll}s, watchdog {Watchdog}, session idle {SessionIdleMin}min",
			options.Eas.MinHeartbeatSeconds, options.Eas.MaxHeartbeatSeconds,
			options.Eas.DefaultWindowSize, options.Eas.MaxWindowSize,
			options.Eas.DavPollSeconds,
			options.Eas.WatchdogSeconds > 0 ? $"{options.Eas.WatchdogSeconds}s" : "off",
			options.Eas.SessionIdleMinutes);
		logger.LogInformation(
			"Auth:     throttle {Throttle}, auth cache {SuccessCache}, negative cache {NegativeCache}",
			options.Auth.MaxFailures > 0
				? $"{options.Auth.MaxFailures} failures/{options.Auth.FailureWindowSeconds}s per address"
				: "OFF",
			options.Auth.SuccessCacheMinutes > 0 ? $"{options.Auth.SuccessCacheMinutes}min" : "off",
			options.Auth.NegativeCacheSeconds > 0 ? $"{options.Auth.NegativeCacheSeconds}s" : "off");
		if (httpsSummary is not null)
			logger.LogInformation("HTTPS:    {State}", httpsSummary);
		if (!string.IsNullOrWhiteSpace(options.PublicUrl))
			logger.LogInformation("Public:   {PublicUrl} (advertised by Autodiscover)", options.PublicUrl);
		if (options.Smtp.ForceFrom)
			logger.LogInformation("SMTP:     From header forced to the authenticated user");
		logger.LogInformation("========================================================");
	}

	/// <summary>
	///   One-line, full-detail description of a declared user — origin, mail address and every
	///   overridden field. Passwords never render; only a masked marker with their format.
	/// </summary>
	internal static string DescribeUser(MergedAccount account)
	{
		AccountOptions o = account.Options;
		List<string> parts =
		[
			account.FromDatabase ? account.ShadowsConfig ? "[db, shadows config]" : "[db]" : "[config]",
		];
		if (!string.IsNullOrWhiteSpace(o.MailAddress))
			parts.Add($"mail={o.MailAddress}");
		if (!string.IsNullOrWhiteSpace(o.Password))
			parts.Add(GatewayPasswordHasher.IsHashed(o.Password)
				? "password=***(pbkdf2)"
				: "password=***(PLAINTEXT)");
		if (o.Imap is not null)
			parts.Add($"imap[{DescribeImapOverride(o.Imap)}]");
		if (o.Smtp is not null)
			parts.Add($"smtp[{DescribeSmtpOverride(o.Smtp)}]");
		if (DescribeDavOverride("caldav", o.CalDav) is { } caldav)
			parts.Add(caldav);
		if (DescribeDavOverride("carddav", o.CardDav) is { } carddav)
			parts.Add(carddav);
		if (parts.Count == 1)
			parts.Add("(allowlist grant — pure pass-through)");
		return string.Join("  ", parts);
	}

	private static string DescribeImapOverride(ImapAccountOptions imap)
	{
		List<string> fields = [];
		if (imap.UserName is not null)
			fields.Add($"user={imap.UserName}");
		if (imap.Password is not null)
			fields.Add(SecretMarker(imap.Password));
		if (imap.Host is not null)
			fields.Add($"host={imap.Host}");
		if (imap.Port is not null)
			fields.Add($"port={imap.Port}");
		if (imap.UseSsl is not null)
			fields.Add($"ssl={imap.UseSsl}");
		if (imap.Security is not null)
			fields.Add($"security={imap.Security}");
		if (imap.AllowInvalidCertificates == true)
			fields.Add("certs=ACCEPT-ANY");
		else if (imap.CaCertificatePath is not null)
			fields.Add("certs=custom-ca");
		if (imap.PathSeparator is not null)
			fields.Add($"sep={imap.PathSeparator}");
		return string.Join(" ", fields);
	}

	private static string DescribeSmtpOverride(SmtpAccountOptions smtp)
	{
		List<string> fields = [];
		if (smtp.UserName is not null)
			fields.Add($"user={smtp.UserName}");
		if (smtp.Password is not null)
			fields.Add(SecretMarker(smtp.Password));
		if (smtp.Host is not null)
			fields.Add($"host={smtp.Host}");
		if (smtp.Port is not null)
			fields.Add($"port={smtp.Port}");
		if (smtp.UseSsl is not null)
			fields.Add($"ssl={smtp.UseSsl}");
		if (smtp.Security is not null)
			fields.Add($"security={smtp.Security}");
		if (smtp.AllowInvalidCertificates == true)
			fields.Add("certs=ACCEPT-ANY");
		else if (smtp.CaCertificatePath is not null)
			fields.Add("certs=custom-ca");
		if (smtp.ForceFrom is not null)
			fields.Add($"forceFrom={smtp.ForceFrom}");
		return string.Join(" ", fields);
	}

	private static string? DescribeDavOverride(string name, DavAccountOptions? dav)
	{
		if (dav is null)
			return null;
		if (dav.Enabled == false)
			return $"{name}=off";
		List<string> fields = [];
		if (dav.UserName is not null)
			fields.Add($"user={dav.UserName}");
		if (dav.Password is not null)
			fields.Add(SecretMarker(dav.Password));
		if (dav.BaseUrl is not null)
			fields.Add($"url={dav.BaseUrl}");
		if (dav.HomeSetPath is not null)
			fields.Add($"homeSet={dav.HomeSetPath}");
		if (dav.TaskFolder is not null)
			fields.Add($"tasks={(dav.TaskFolder.Length == 0 ? "(off)" : dav.TaskFolder)}");
		if (dav.AllowInvalidCertificates == true)
			fields.Add("certs=ACCEPT-ANY");
		else if (dav.CaCertificatePath is not null)
			fields.Add("certs=custom-ca");
		return $"{name}[{string.Join(" ", fields)}]";
	}

	private static string SecretMarker(string value)
	{
		return SecretValue.IsSealed(value) ? "pw=***(sealed)" : "pw=***(PLAINTEXT)";
	}

	private static string DescribeDav(DavServerOptions? dav)
	{
		if (dav is null)
			return "local storage (gateway database)";
		string? homeSet = string.IsNullOrEmpty(dav.HomeSetPath) ? "RFC 6764 discovery" : dav.HomeSetPath;
		string shared = dav.SharedCollections is { Count: > 0 } list
			? $", shared: {list.Count} configured"
			: "";
		return
			$"{dav.BaseUrl}  (home set: {homeSet}, certs={CertMode(dav.AllowInvalidCertificates, dav.CaCertificatePath)}{shared})";
	}

	/// <summary>Certificate-validation mode marker; ACCEPT-ANY shouts on purpose.</summary>
	private static string CertMode(bool allowInvalid, string? caPath)
	{
		return allowInvalid ? "ACCEPT-ANY" : string.IsNullOrWhiteSpace(caPath) ? "system" : "custom-ca";
	}

	internal static string Redact(string provider, string connectionString)
	{
		// URI form (postgresql://user:pass@host/db?password=…): mask the userinfo password
		// and any password query parameter, keep host/port/database for diagnostics.
		if (PostgresConnectionUri.IsPostgresUri(connectionString))
			return UriQueryPassword().Replace(
				UriUserInfoPassword().Replace(connectionString, "$1$2:***@"), "$1=***");
		// SQLite connection strings are just a file path — nothing to hide.
		if (provider.StartsWith("sqlite", StringComparison.OrdinalIgnoreCase))
			return connectionString;
		// Postgres/other: mask the password keyword's value, keep everything else for diagnostics.
		return PasswordKeyword().Replace(connectionString, "$1=***");
	}

	[GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*[^;]*")]
	private static partial Regex PasswordKeyword();

	[GeneratedRegex(@"(?i)^(jdbc:)?(postgres(?:ql)?://[^:/?#@]*):[^@]*@", RegexOptions.None)]
	private static partial Regex UriUserInfoPassword();

	[GeneratedRegex(@"(?i)([?&](?:password|pwd))=[^&]*")]
	private static partial Regex UriQueryPassword();
}
