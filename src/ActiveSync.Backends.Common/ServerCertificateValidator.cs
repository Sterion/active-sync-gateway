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
	private static readonly ConcurrentDictionary<string, X509Certificate2Collection> CaCache =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	///   Builds the validation callback, or null when neither knob is set so the caller
	///   keeps the platform's default validation untouched.
	/// </summary>
	public static RemoteCertificateValidationCallback? CreateCallback(
		bool allowInvalidCertificates, string? caCertificatePath)
	{
		if (allowInvalidCertificates)
			return (_, _, _, _) => true;
		if (string.IsNullOrWhiteSpace(caCertificatePath))
			return null;

		X509Certificate2Collection cas = LoadCaCertificates(caCertificatePath);
		return (_, certificate, _, errors) =>
			Validate(certificate as X509Certificate2, errors, false, cas);
	}

	/// <summary>Core decision, exposed for tests.</summary>
	public static bool Validate(
		X509Certificate2? certificate,
		SslPolicyErrors errors,
		bool allowInvalid,
		X509Certificate2Collection? customCas)
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
		chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
		return chain.Build(certificate);
	}

	/// <summary>Loads (and caches) the CA PEM file. Throws with a clear message when unreadable.</summary>
	public static X509Certificate2Collection LoadCaCertificates(string path)
	{
		return CaCache.GetOrAdd(path, p =>
		{
			X509Certificate2Collection collection = new();
			try
			{
				collection.ImportFromPemFile(p);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException(
					$"CaCertificatePath '{p}' could not be loaded as PEM certificates: {ex.Message}", ex);
			}

			if (collection.Count == 0)
				throw new InvalidOperationException($"CaCertificatePath '{p}' contains no certificates.");
			return collection;
		});
	}
}
