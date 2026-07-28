using System.Security.Cryptography;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Security;
using ActiveSync.Crypto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Options;

/// <summary>
///   Startup validation of the HOST options (database, EAS, auth, encryption, policy, ...).
///   Backend role sections and declared users are validated by
///   <see cref="BackendConfigurationValidator" /> after the service provider is built — it
///   needs the provider registry so each provider can validate its own settings.
/// </summary>
/// <param name="configuration">
///   Optional — only needed for the cross-listener port check, which compares
///   <see cref="TlsOptions.Port" />/<see cref="MetricsOptions.Port" /> against the base Kestrel
///   HTTP endpoint (<c>Kestrel:Endpoints:Http:Url</c> / <c>ASPNETCORE_URLS</c>), neither of which
///   is part of <see cref="ActiveSyncOptions" /> itself. Null (every call site historically
///   constructed this parameterlessly) simply skips that one check.
/// </param>
public sealed class ActiveSyncOptionsValidator(IConfiguration? configuration = null) : IValidateOptions<ActiveSyncOptions>
{
	public ValidateOptionsResult Validate(string? name, ActiveSyncOptions options)
	{
		List<string> failures = new();

		if (string.IsNullOrWhiteSpace(options.Database.ConnectionString))
			failures.Add("ActiveSync:Database:ConnectionString is required.");
		else if (PostgresConnectionUri.IsPostgresUri(options.Database.ConnectionString) &&
		         !PostgresConnectionUri.TryConvert(options.Database.ConnectionString, out _, out string? uriError))
			failures.Add(uriError!);

		if (options.Eas.MinHeartbeatSeconds < 1 ||
		    options.Eas.MaxHeartbeatSeconds < options.Eas.MinHeartbeatSeconds ||
		    options.Eas.MaxHeartbeatSeconds > 3540)
			failures.Add("ActiveSync:Eas:MinHeartbeatSeconds/ActiveSync:Eas:MaxHeartbeatSeconds are invalid " +
			             "(need 1 <= Min <= Max <= 3540).");

		if (options.Eas.WatchdogSeconds is < 0 or > 0 and < 15)
			failures.Add("ActiveSync:Eas:WatchdogSeconds must be 0 (disabled) or at least 15.");
		if (options.Eas.FolderRetentionDays is < 0 or > 3650)
			failures.Add("ActiveSync:Eas:FolderRetentionDays must be between 0 (disabled) and 3650.");
		if (options.Eas.SendDedupRetentionDays is < 0 or > 3650)
			failures.Add("ActiveSync:Eas:SendDedupRetentionDays must be between 0 (disabled) and 3650.");

		// Mirror the three SettingKeys catalogue bounds this validator never checked, so a
		// file/env typo is held to the same range the identical `eas config set` write already is.
		if (options.Eas.MaxPingFolders is < 0 or > 65535)
			failures.Add("ActiveSync:Eas:MaxPingFolders must be between 0 (disabled) and 65535.");

		// Mirror the SettingKeys catalogue bounds so file/env values are held to the same range a
		// CLI/web write is — otherwise a typo (DefaultWindowSize=0 → empty Sync responses) starts clean.
		if (options.Eas.DavPollSeconds is < 1 or > 86400)
			failures.Add("ActiveSync:Eas:DavPollSeconds must be between 1 and 86400.");
		if (options.Eas.MaxWindowSize is < 1 or > 522)
			failures.Add("ActiveSync:Eas:MaxWindowSize must be between 1 and 522.");
		if (options.Eas.DefaultWindowSize is < 1 or > 522)
			failures.Add("ActiveSync:Eas:DefaultWindowSize must be between 1 and 522.");
		if (options.Eas.DefaultWindowSize > options.Eas.MaxWindowSize)
			failures.Add("ActiveSync:Eas:DefaultWindowSize must not exceed ActiveSync:Eas:MaxWindowSize.");
		if (options.Eas.SessionIdleMinutes is < 1 or > 1440)
			failures.Add("ActiveSync:Eas:SessionIdleMinutes must be between 1 and 1440.");

		if (options.Auth.MaxFailures is < 0 or > 1000000)
			failures.Add("ActiveSync:Auth:MaxFailures must be between 0 (disabled) and 1000000.");
		if (options.Auth.FailureWindowSeconds is < 1 or > 86400)
			failures.Add("ActiveSync:Auth:FailureWindowSeconds must be between 1 and 86400.");
		if (options.Auth.NegativeCacheSeconds is < 0 or > 86400)
			failures.Add("ActiveSync:Auth:NegativeCacheSeconds must be between 0 (disabled) and 86400.");
		if (options.Auth.SuccessCacheMinutes is < 0 or > 1440)
			failures.Add("ActiveSync:Auth:SuccessCacheMinutes must be between 0 (disabled) and 1440.");
		if (options.Auth.UsersRefreshSeconds is < 0 or > 86400)
			failures.Add("ActiveSync:Auth:UsersRefreshSeconds must be between 0 and 86400.");

		if (!string.IsNullOrWhiteSpace(options.PublicUrl) &&
		    (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out Uri? publicUri) ||
		     (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps)))
			failures.Add($"ActiveSync:PublicUrl '{options.PublicUrl}' must be an absolute http(s) URL.");

		ValidateTls(options.Tls, failures);

		ValidatePolicy(options.Policy, failures);
		ValidateMetrics(options.Metrics, failures);
		ValidateListeners(options.Tls, options.Metrics, failures);
		ValidateWebUi(options.WebUi, failures);

		if (options.Log.Mode.ToLowerInvariant() is not ("simple" or "standard" or "extended"))
			failures.Add($"ActiveSync:Log:Mode '{options.Log.Mode}' is unknown (use Simple, Standard or Extended).");
		if (options.Log.Format.ToLowerInvariant() is not ("text" or "json"))
			failures.Add($"ActiveSync:Log:Format '{options.Log.Format}' is unknown (use Text or Json).");
		// Shares LogQueryService's alias table (info/warn/critical) rather than matching only
		// the four exact names — `eas logs -l critical` already accepted the alias; a config file
		// carrying the same value must not brick startup here just because this check didn't know it.
		if (LogQueryService.NormalizeLevelName(options.Log.DbMinimumLevel) is null)
			failures.Add($"ActiveSync:Log:DbMinimumLevel '{options.Log.DbMinimumLevel}' is unknown " +
			             "(use Information, Warning, Error or Fatal, or an alias like Info/Warn/Critical).");
		if (options.Log.RetentionDays is < 0 or > 3650)
			failures.Add("ActiveSync:Log:RetentionDays must be between 0 (disabled) and 3650.");

		ValidateEncryption(options.Encryption, failures);

		return failures.Count > 0
			? ValidateOptionsResult.Fail(failures)
			: ValidateOptionsResult.Success;
	}

	private static void ValidatePolicy(PolicyOptions policy, List<string> failures)
	{
		// Ranges from MS-ASPROV 2.2.2; validated even when disabled so a typo surfaces
		// before the operator flips Enabled and re-provisions the whole fleet with it.
		if (policy.MinDevicePasswordLength is < 1 or > 16)
			failures.Add(
				$"ActiveSync:Policy:MinDevicePasswordLength {policy.MinDevicePasswordLength} is out of range (1-16).");
		if (policy.MinDevicePasswordComplexCharacters is < 1 or > 4)
			failures.Add(
				$"ActiveSync:Policy:MinDevicePasswordComplexCharacters {policy.MinDevicePasswordComplexCharacters} is out of range (1-4).");
		if (policy.MaxInactivityTimeDeviceLock is < 1 or > 9999)
			failures.Add(
				$"ActiveSync:Policy:MaxInactivityTimeDeviceLock {policy.MaxInactivityTimeDeviceLock} is out of range (1-9999 seconds).");
		if (policy.MaxDevicePasswordFailedAttempts is < 4 or > 16)
			failures.Add(
				$"ActiveSync:Policy:MaxDevicePasswordFailedAttempts {policy.MaxDevicePasswordFailedAttempts} is out of range (4-16).");
		if (policy.DevicePasswordExpiration is < 0)
			failures.Add(
				"ActiveSync:Policy:DevicePasswordExpiration must be 0 (never expires) or a positive number of days.");
		if (policy.DevicePasswordHistory is < 0)
			failures.Add("ActiveSync:Policy:DevicePasswordHistory must be 0 or positive.");
		if (policy.MaxAttachmentSize is < 0)
			failures.Add("ActiveSync:Policy:MaxAttachmentSize must be 0 or a positive number of bytes.");
	}

	private static void ValidateEncryption(EncryptionOptions encryption, List<string> failures)
	{
		byte[]? key = EncryptionKeyLoader.TryLoadKey(encryption, out string? error);
		if (error is not null)
		{
			failures.Add(error);
			return;
		}

		if (key is null && !encryption.AllowPlaintext)
			failures.Add(
				"ActiveSync:Encryption:Key (or KeyFile) is required — local contact/calendar/task/note " +
				"content is encrypted at rest. Generate a key with 'openssl rand -base64 32', or set " +
				"ActiveSync:Encryption:AllowPlaintext=true to explicitly run unencrypted (dev/test only).");
		if (key is not null)
			CryptographicOperations.ZeroMemory(key);
	}

	private static void ValidateMetrics(MetricsOptions metrics, List<string> failures)
	{
		if (metrics.Port is { } port and (< 1 or > 65535))
			failures.Add($"ActiveSync:Metrics:Port {port} is out of range (1-65535).");
	}

	/// <summary>
	///   `eas config set` accepted a Tls:Port/Metrics:Port that collides with another listener,
	///   and the NEXT start died on bind (Kestrel's ListenAnyIP calls in
	///   Program.ConfigureHosting) — nothing compared the two dedicated ports against EACH OTHER or
	///   against the base HTTP endpoint every deployment already has. Tls:Port is only a real
	///   listener while Tls is Enabled (see ConfigureHosting); Metrics:Port only while Metrics is
	///   Enabled with a Port actually set (unset shares the main listeners instead).
	/// </summary>
	private void ValidateListeners(TlsOptions tls, MetricsOptions metrics, List<string> failures)
	{
		bool tlsListens = tls.Enabled;
		int? metricsListenPort = metrics is { Enabled: true, Port: { } p } ? p : null;

		if (tlsListens && metricsListenPort == tls.Port)
			failures.Add(
				$"ActiveSync:Tls:Port and ActiveSync:Metrics:Port are both {tls.Port} — two dedicated " +
				"listeners cannot share one port; the next start will fail to bind.");

		if (BaseHttpPort() is not { } httpPort)
			return;

		if (tlsListens && tls.Port == httpPort)
			failures.Add(
				$"ActiveSync:Tls:Port {tls.Port} collides with the base HTTP listener port {httpPort} " +
				"(Kestrel:Endpoints:Http:Url / ASPNETCORE_URLS) — the next start will fail to bind.");
		if (metricsListenPort == httpPort)
			failures.Add(
				$"ActiveSync:Metrics:Port {metricsListenPort} collides with the base HTTP listener port " +
				$"{httpPort} (Kestrel:Endpoints:Http:Url / ASPNETCORE_URLS) — the next start will fail to bind.");
	}

	/// <summary>
	///   The port Kestrel binds its plain-HTTP endpoint on, read the same way the container
	///   healthcheck derives it (<see cref="ActiveSync.Server.Cli.CliVerbs" />): the
	///   <c>Kestrel:Endpoints:Http:Url</c> configuration key (set by the shipped appsettings.json,
	///   or an operator override) or the <c>ASPNETCORE_URLS</c> variable — both visible through
	///   <see cref="configuration" /> when it carries the environment-variables provider, which
	///   every real caller's does. Deliberately does NOT fall back to reading the process
	///   environment directly when no <see cref="configuration" /> was supplied: every existing
	///   caller of the parameterless constructor must stay fully unaffected by whatever
	///   ASPNETCORE_URLS happens to be set to in that process (a test runner's, for one) — no
	///   configuration means this one check is skipped, not guessed at.
	/// </summary>
	private int? BaseHttpPort()
	{
		string? url = configuration?["Kestrel:Endpoints:Http:Url"]
		              ?? configuration?["ASPNETCORE_URLS"]?.Split(';')[0];
		return url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Port : null;
	}

	private static void ValidateTls(TlsOptions tls, List<string> failures)
	{
		if (!tls.Enabled)
			return;
		if (tls.Port is < 1 or > 65535)
			failures.Add($"ActiveSync:Tls:Port {tls.Port} is out of range (1-65535).");

		bool hasCert = !string.IsNullOrWhiteSpace(tls.CertificatePath);
		if (!string.IsNullOrWhiteSpace(tls.CertificateKeyPath) && !hasCert)
			failures.Add(
				"ActiveSync:Tls:CertificateKeyPath is set without ActiveSync:Tls:CertificatePath.");
		// Existence is checked here (fail-fast, clear message); the certificate is actually loaded
		// at startup by TlsCertificateResolver, which surfaces parse/key-mismatch errors.
		if (hasCert && !File.Exists(tls.CertificatePath))
			failures.Add($"ActiveSync:Tls:CertificatePath '{tls.CertificatePath}' does not exist.");
		if (!string.IsNullOrWhiteSpace(tls.CertificateKeyPath) && !File.Exists(tls.CertificateKeyPath))
			failures.Add($"ActiveSync:Tls:CertificateKeyPath '{tls.CertificateKeyPath}' does not exist.");
	}

	private static void ValidateWebUi(WebUiOptions webUi, List<string> failures)
	{
		// A disabled OIDC block is inert — its settings are kept but ignored — so don't hold up
		// startup over a half-filled configuration the operator has switched off.
		if (webUi.Oidc is not { Enabled: true } oidc)
			return;
		// Any client/authority field present signals OIDC intent — then the pair is required.
		bool intended = !string.IsNullOrWhiteSpace(oidc.Authority) ||
		                !string.IsNullOrWhiteSpace(oidc.ClientId) ||
		                !string.IsNullOrWhiteSpace(oidc.ClientSecret);
		if (intended)
		{
			if (string.IsNullOrWhiteSpace(oidc.Authority))
				failures.Add("ActiveSync:WebUi:Oidc:Authority is required when OIDC is configured.");
			else if (!Uri.TryCreate(oidc.Authority, UriKind.Absolute, out Uri? authority) ||
			         (authority.Scheme != Uri.UriSchemeHttp && authority.Scheme != Uri.UriSchemeHttps))
				failures.Add($"ActiveSync:WebUi:Oidc:Authority '{oidc.Authority}' must be an absolute http(s) URL.");
			if (string.IsNullOrWhiteSpace(oidc.ClientId))
				failures.Add("ActiveSync:WebUi:Oidc:ClientId is required when OIDC is configured.");
			if (string.IsNullOrWhiteSpace(oidc.LoginClaim))
				failures.Add("ActiveSync:WebUi:Oidc:LoginClaim must not be empty.");
		}

		if (!string.IsNullOrWhiteSpace(oidc.AdminClaimValue) && string.IsNullOrWhiteSpace(oidc.AdminClaim))
			failures.Add("ActiveSync:WebUi:Oidc:AdminClaimValue requires AdminClaim to be set.");
		// The reverse omission used to mean "any value grants admin", which turns the obvious
		// AdminClaim: "groups" into a grant of gateway admin to the entire directory. "Any
		// value" now has to be spelled out as "*" so it cannot be reached by leaving a field out.
		if (!string.IsNullOrWhiteSpace(oidc.AdminClaim) && string.IsNullOrWhiteSpace(oidc.AdminClaimValue))
			failures.Add(
				"ActiveSync:WebUi:Oidc:AdminClaimValue is required when AdminClaim is set — it is the " +
				"value that grants admin. Use \"*\" only if ANY value of the claim should grant it.");
	}
}
