using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Options;

/// <summary>
///   Startup validation: IMAP and SMTP are mandatory (the gateway is pointless without mail
///   access); CalDAV/CardDAV are optional but must be well-formed when configured.
/// </summary>
public sealed class ActiveSyncOptionsValidator : IValidateOptions<ActiveSyncOptions>
{
	public ValidateOptionsResult Validate(string? name, ActiveSyncOptions options)
	{
		List<string> failures = new();

		if (string.IsNullOrWhiteSpace(options.Imap.Host))
			failures.Add("ActiveSync:Imap:Host is required — this gateway cannot run without mail access.");
		if (string.IsNullOrWhiteSpace(options.Smtp.Host))
			failures.Add("ActiveSync:Smtp:Host is required — this gateway cannot run without mail access.");

		if (options.Imap.Port is < 1 or > 65535)
			failures.Add($"ActiveSync:Imap:Port {options.Imap.Port} is out of range (1-65535).");
		if (options.Smtp.Port is < 1 or > 65535)
			failures.Add($"ActiveSync:Smtp:Port {options.Smtp.Port} is out of range (1-65535).");

		ValidateAccounts(options, failures);

		ValidateDav(options.CalDav, "CalDav", failures);
		ValidateDav(options.CardDav, "CardDav", failures);

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

		if (options.SelfSignedTls.Enabled && options.SelfSignedTls.Port is < 1 or > 65535)
			failures.Add($"ActiveSync:SelfSignedTls:Port {options.SelfSignedTls.Port} is out of range (1-65535).");

		ValidatePolicy(options.Policy, failures);

		if (options.Log.Mode.ToLowerInvariant() is not ("simple" or "standard" or "extended"))
			failures.Add($"ActiveSync:Log:Mode '{options.Log.Mode}' is unknown (use Simple, Standard or Extended).");
		if (options.Log.Format.ToLowerInvariant() is not ("text" or "json"))
			failures.Add($"ActiveSync:Log:Format '{options.Log.Format}' is unknown (use Text or Json).");

		ValidateEncryption(options.Encryption, failures);

		if (options.Sieve.Port is < 1 or > 65535)
			failures.Add($"ActiveSync:Sieve:Port {options.Sieve.Port} is out of range (1-65535).");

		ValidateCaPath(options.Imap.CaCertificatePath, "Imap", failures);
		ValidateCaPath(options.Smtp.CaCertificatePath, "Smtp", failures);
		ValidateCaPath(options.CalDav?.CaCertificatePath, "CalDav", failures);
		ValidateCaPath(options.CardDav?.CaCertificatePath, "CardDav", failures);
		ValidateCaPath(options.Sieve.CaCertificatePath, "Sieve", failures);

		return failures.Count > 0
			? ValidateOptionsResult.Fail(failures)
			: ValidateOptionsResult.Success;
	}

	private static void ValidateAccounts(ActiveSyncOptions options, List<string> failures)
	{
		// RequireDeclaredUsers with an empty config Users list is NOT a validation failure:
		// declared users may live in the state database (eas user add), which the validator
		// cannot see. StartupSummary warns when the MERGED set is also empty.
		if (options.Users is not { Count: > 0 })
			return;

		// ValidateUsers appends per-user failures (merge completeness, sealed values,
		// logins); pass the key only if it loads — key problems are already reported by
		// ValidateEncryption, and any enc: values are then flagged as unresolvable.
		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Encryption, out string? _);
		AccountResolver.ValidateUsers(options, key, failures);
		if (key is not null)
			CryptographicOperations.ZeroMemory(key);

		// Effective CA paths per user (globals are validated below as usual).
		foreach ((string login, AccountOptions account) in options.Users)
		{
			ValidateCaPath(account.Imap?.CaCertificatePath, $"Users:{login}:Imap", failures);
			ValidateCaPath(account.Smtp?.CaCertificatePath, $"Users:{login}:Smtp", failures);
			ValidateCaPath(account.CalDav?.CaCertificatePath, $"Users:{login}:CalDav", failures);
			ValidateCaPath(account.CardDav?.CaCertificatePath, $"Users:{login}:CardDav", failures);
		}
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

	private static void ValidateCaPath(string? path, string sectionName, List<string> failures)
	{
		if (string.IsNullOrWhiteSpace(path))
			return;
		if (!File.Exists(path))
		{
			failures.Add($"ActiveSync:{sectionName}:CaCertificatePath '{path}' does not exist.");
			return;
		}

		try
		{
			X509Certificate2Collection collection = new();
			collection.ImportFromPemFile(path);
			if (collection.Count == 0)
				failures.Add($"ActiveSync:{sectionName}:CaCertificatePath '{path}' contains no certificates.");
		}
		catch (Exception ex)
		{
			failures.Add(
				$"ActiveSync:{sectionName}:CaCertificatePath '{path}' is not a valid PEM certificate file: {ex.Message}");
		}
	}

	private static void ValidateDav(DavServerOptions? dav, string sectionName, List<string> failures)
	{
		if (dav is null)
			return; // absent section = feature disabled, perfectly fine
		if (!Uri.TryCreate(dav.BaseUrl, UriKind.Absolute, out Uri? uri) ||
		    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			failures.Add(
				$"ActiveSync:{sectionName}:BaseUrl '{dav.BaseUrl}' must be an absolute http(s) URL " +
				$"(or remove the {sectionName} section entirely to disable it).");
		if (dav.CalendarAttachments.ToLowerInvariant() is not ("auto" or "on" or "off"))
			failures.Add(
				$"ActiveSync:{sectionName}:CalendarAttachments '{dav.CalendarAttachments}' is unknown " +
				"(use Auto, On or Off).");
	}
}
