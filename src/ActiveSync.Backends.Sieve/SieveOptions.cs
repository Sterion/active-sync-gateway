using ActiveSync.Backends.Common;

namespace ActiveSync.Backends.Sieve;

/// <summary>
///   ManageSieve (RFC 5804) connection for the out-of-office feature: when the Oof role is
///   assigned to the "sieve" provider, the Settings→Oof command manages a gateway-owned
///   sieve vacation script on this server. No Oof role = the historical no-op stub.
///   TLS-trust knobs come from <see cref="NetworkBackendOptions" />.
/// </summary>
public sealed class SieveOptions : NetworkBackendOptions
{
	/// <summary>ManageSieve host (required — no implicit "same as IMAP" default).</summary>
	public string? Host { get; set; }

	public int Port { get; set; } = 4190;

	/// <summary>
	///   Require STARTTLS before authenticating (ManageSieve has no implicit-TLS port).
	///   false = plaintext, for test stacks only.
	/// </summary>
	public bool UseTls { get; set; } = true;
}
