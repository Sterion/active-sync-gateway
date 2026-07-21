using System.Text.RegularExpressions;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The single home for deciding what counts as a secret and how it is masked. It replaces the
///   four independent, drifting implementations the review found (S7): the CLI's
///   <c>ConfigCommands.Mask</c>, the startup banner's connection-string <c>Redact</c>, the web
///   backends editor's <c>SecretMask</c>, and every ad-hoc caller that reinvented masking (K37).
///   One notion of "secret" (a name heuristic covering passwords, tokens, API keys and client
///   secrets), one mask token, and one connection-string redactor used everywhere a secret can
///   leak into a log line, a banner, a CLI echo or an API response.
/// </summary>
public static partial class SecretRedaction
{
	/// <summary>The one mask token every surface uses for a hidden secret value.</summary>
	public const string Mask = "***";

	// Substrings that mark a configuration leaf (or full key) as carrying a secret. Matched
	// case-insensitively anywhere in the name so both "Password" and "OAuthToken" are caught; the
	// list is deliberately conservative — bare "key" is excluded because it collides with file
	// paths (CertificateKeyPath) and non-secret identifiers.
	private static readonly string[] SecretMarkers =
	[
		"password", "passwd", "pwd", "passphrase", "secret", "token", "apikey", "credential"
	];

	/// <summary>
	///   True when a configuration key or leaf name looks like it holds a secret value. Used by
	///   every surface that enumerates settings (CLI list/get/set, the startup banner, the web
	///   settings and backends editors, per-account overrides) so they agree on what to hide.
	/// </summary>
	public static bool IsSecretName(string? name)
	{
		if (string.IsNullOrEmpty(name))
			return false;
		foreach (string marker in SecretMarkers)
			if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	/// <summary>Masks a value when its key name looks like a secret; passes non-secrets and nulls through unchanged.</summary>
	public static string? MaskIfSecret(string name, string? value)
	{
		return value is not null && IsSecretName(name) ? Mask : value;
	}

	/// <summary>
	///   Redacts a password embedded in a database connection string, keeping host/port/database
	///   for diagnostics. Covers the URI userinfo form (<c>postgres://user:pass@…</c>), a password
	///   query parameter, and the keyword form (<c>…;Password=…</c>) — for every provider, SQLite
	///   included: SQLCipher and encrypted-SQLite connection strings accept a <c>Password=</c>
	///   keyword, so a bare file path is returned verbatim while an embedded password is masked
	///   (E23 — the old code short-circuited SQLite as "just a file path, nothing to hide").
	/// </summary>
	public static string RedactConnectionString(string? connectionString)
	{
		if (string.IsNullOrEmpty(connectionString))
			return connectionString ?? "";
		if (PostgresConnectionUri.IsPostgresUri(connectionString))
			return UriQueryPassword().Replace(
				UriUserInfoPassword().Replace(connectionString, "$1$2:***@"), "$1=***");
		// Keyword form, used by Npgsql and by SQLite/SQLCipher alike: mask any Password/Pwd value,
		// leave a password-free file path untouched.
		return PasswordKeyword().Replace(connectionString, "$1=***");
	}

	[GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*[^;]*")]
	private static partial Regex PasswordKeyword();

	[GeneratedRegex(@"(?i)^(jdbc:)?(postgres(?:ql)?://[^:/?#@]*):[^@]*@", RegexOptions.None)]
	private static partial Regex UriUserInfoPassword();

	[GeneratedRegex(@"(?i)([?&](?:password|pwd))=[^&]*")]
	private static partial Regex UriQueryPassword();
}
