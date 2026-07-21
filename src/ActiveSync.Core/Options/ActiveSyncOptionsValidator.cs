using System.Security.Cryptography;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Options;

/// <summary>
///   Startup validation of the HOST options (database, EAS, auth, encryption, policy, ...).
///   Backend role sections and declared users are validated by
///   <see cref="BackendConfigurationValidator" /> after the service provider is built — it
///   needs the provider registry so each provider can validate its own settings.
/// </summary>
public sealed class ActiveSyncOptionsValidator : IValidateOptions<ActiveSyncOptions>
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
			failures.Add("ActiveSync:Eas heartbeat bounds are invalid (need 1 <= Min <= Max <= 3540).");

		if (options.Eas.WatchdogSeconds is < 0 or > 0 and < 15)
			failures.Add("ActiveSync:Eas:WatchdogSeconds must be 0 (disabled) or at least 15.");

		if (options.Auth.MaxFailures < 0)
			failures.Add("ActiveSync:Auth:MaxFailures must be 0 (disabled) or positive.");
		if (options.Auth.FailureWindowSeconds < 1)
			failures.Add("ActiveSync:Auth:FailureWindowSeconds must be at least 1.");
		if (options.Auth.NegativeCacheSeconds < 0)
			failures.Add("ActiveSync:Auth:NegativeCacheSeconds must be 0 (disabled) or positive.");
		if (options.Auth.SuccessCacheMinutes < 0)
			failures.Add("ActiveSync:Auth:SuccessCacheMinutes must be 0 (disabled) or positive.");

		if (!string.IsNullOrWhiteSpace(options.PublicUrl) &&
		    (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out Uri? publicUri) ||
		     (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps)))
			failures.Add($"ActiveSync:PublicUrl '{options.PublicUrl}' must be an absolute http(s) URL.");

		ValidateTls(options.Tls, failures);

		ValidatePolicy(options.Policy, failures);
		ValidateMetrics(options.Metrics, failures);
		ValidateWebUi(options.WebUi, failures);

		if (options.Log.Mode.ToLowerInvariant() is not ("simple" or "standard" or "extended"))
			failures.Add($"ActiveSync:Log:Mode '{options.Log.Mode}' is unknown (use Simple, Standard or Extended).");
		if (options.Log.Format.ToLowerInvariant() is not ("text" or "json"))
			failures.Add($"ActiveSync:Log:Format '{options.Log.Format}' is unknown (use Text or Json).");
		if (options.Log.DbMinimumLevel.ToLowerInvariant() is not ("information" or "warning" or "error" or "fatal"))
			failures.Add($"ActiveSync:Log:DbMinimumLevel '{options.Log.DbMinimumLevel}' is unknown " +
			             "(use Information, Warning, Error or Fatal).");
		if (options.Log.RetentionDays < 0)
			failures.Add("ActiveSync:Log:RetentionDays must be 0 or greater.");

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
