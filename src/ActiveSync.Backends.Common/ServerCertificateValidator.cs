using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ActiveSync.Backends.Common;

/// <summary>
///   The single source of TLS certificate-validation callbacks for every backend connection
///   (IMAP, SMTP, CalDAV, CardDAV — MailKit and SocketsHttpHandler share the delegate type).
///   Modes: default OS validation (no knobs), accept-everything (AllowInvalidCertificates,
///   lab use), or system trust extended with a custom CA PEM file (private PKI).
/// </summary>
public static class ServerCertificateValidator
{
	// Keyed on (path, last-write time, length) rather than the path alone. CaCertificatePath
	// is a live, DB-settable option that otherwise applies on session recycle (~1 s, per AGENTS.md)
	// — a path-only key meant a rotated CA bundle at the same path was cached FOREVER, including
	// past the point the old root expires, with no way for an operator to fix it short of a
	// process restart. A stat per handler creation is negligible next to a TLS handshake.
	private static readonly ConcurrentDictionary<(string Path, DateTime LastWriteUtc, long Length),
		X509Certificate2Collection> CaCache = new();

	/// <summary>
	///   Builds the validation callback, or null when neither knob is set so the caller
	///   keeps the platform's default validation untouched.
	/// </summary>
	public static RemoteCertificateValidationCallback? CreateCallback(
		bool allowInvalidCertificates, string? caCertificatePath, bool checkRevocation = false)
	{
		if (allowInvalidCertificates)
			return (_, _, _, _) => true;
		if (string.IsNullOrWhiteSpace(caCertificatePath))
			return null;

		X509Certificate2Collection cas = LoadCaCertificates(caCertificatePath);
		// Thread the TLS stack's OWN chain (built from whatever the server presented during
		// the handshake) through to Validate instead of discarding it — that chain is the only
		// place an intermediate certificate the server sent is ever available.
		return (_, certificate, chain, errors) =>
			Validate(certificate as X509Certificate2, errors, false, cas, checkRevocation, chain);
	}

	/// <summary>Core decision, exposed for tests.</summary>
	public static bool Validate(
		X509Certificate2? certificate,
		SslPolicyErrors errors,
		bool allowInvalid,
		X509Certificate2Collection? customCas,
		bool checkRevocation = false,
		X509Chain? handshakeChain = null)
	{
		if (allowInvalid)
			return true;
		if (errors == SslPolicyErrors.None)
			return true;
		// Name mismatches and missing certificates are never repaired by a custom root.
		if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
		                        || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
			return false;
		if (customCas is not { Count: > 0 })
			return false;

		using X509Chain chain = new();
		chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
		chain.ChainPolicy.CustomTrustStore.AddRange(customCas);
		// Seed ExtraStore with whatever intermediates the TLS handshake itself presented —
		// the chain argument the callback used to discard — so a leaf signed by a private
		// intermediate (not directly by the trusted root) can still complete the chain. Without
		// this, CaCertificatePath's documented "private PKI" use case fails closed whenever the
		// server's cert isn't signed directly by the trusted root, unless the intermediate
		// happens to be cached locally or fetchable via AIA.
		if (handshakeChain is not null)
			foreach (X509ChainElement element in handshakeChain.ChainElements)
				if (!element.Certificate.Equals(certificate))
					chain.ChainPolicy.ExtraStore.Add(element.Certificate);
		// NoCheck by default — most private CAs behind a custom-CA path publish no CRL/OCSP,
		// so unconditionally checking would fail every connection closed. CheckRevocation is the
		// operator's explicit opt-in for a private PKI that DOES publish revocation, so a revoked
		// backend certificate is no longer silently accepted just because it chains to a trusted CA.
		chain.ChainPolicy.RevocationMode =
			checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
		return chain.Build(certificate);
	}

	/// <summary>
	///   Loads (and caches) the CA PEM file. Throws with a clear message when unreadable. The cache
	///   key folds in the file's last-write time and length so a rotated bundle at the same
	///   path is picked up on the next call rather than serving the collection loaded years ago.
	/// </summary>
	public static X509Certificate2Collection LoadCaCertificates(string path)
	{
		FileInfo info = new(path);
		(string Path, DateTime LastWriteUtc, long Length) key =
			(path, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue, info.Exists ? info.Length : -1);

		return CaCache.GetOrAdd(key, _ =>
		{
			X509Certificate2Collection collection = new();
			try
			{
				collection.ImportFromPemFile(path);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException(
					$"CaCertificatePath '{path}' could not be loaded as PEM certificates: {ex.Message}", ex);
			}

			if (collection.Count == 0)
				throw new InvalidOperationException($"CaCertificatePath '{path}' contains no certificates.");
			return collection;
		});
	}
}
