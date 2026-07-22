using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Options;
using ActiveSync.Crypto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Security;

/// <summary>Where the gateway's active HTTPS certificate comes from.</summary>
public enum TlsCertificateSource
{
	/// <summary>HTTPS is off (<c>ActiveSync:Tls:Enabled=false</c>) — terminate TLS in front.</summary>
	Disabled,

	/// <summary>The built-in self-signed certificate, generated once and stored in the database.</summary>
	SelfSigned,

	/// <summary>An operator-supplied certificate mounted from a file (PEM or PFX).</summary>
	External,
}

/// <summary>
///   A read-only, private-key-free description of the gateway's active HTTPS certificate — what
///   the admin TLS panel and <c>eas tls</c> show. Populated from whichever certificate would be
///   served (external file when configured, else the stored self-signed one).
/// </summary>
public sealed record TlsCertificateInfo(
	bool Enabled,
	int Port,
	TlsCertificateSource Source,
	string? CertificatePath,
	string? Subject,
	string? Issuer,
	IReadOnlyList<string> SubjectAlternativeNames,
	DateTime? NotBeforeUtc,
	DateTime? NotAfterUtc,
	string? Fingerprint,
	string? KeyAlgorithm,
	int? KeySize,
	string? Error);

/// <summary>
///   Resolves the gateway's HTTPS certificate from <c>ActiveSync:Tls</c>. Two modes:
///   an operator-supplied file (<see cref="TlsOptions.CertificatePath" /> — PEM pair or PFX,
///   e.g. a cert-manager/ACME mount) or, when none is set, the self-signed certificate from
///   <see cref="GatewayCertificateStore" />. Everything is read once at startup — Kestrel binds
///   the listener and loads the certificate then, and a rotated mount takes effect on restart
///   (matching how Kubernetes mounts and Kestrel both behave).
/// </summary>
public sealed class TlsCertificateResolver(
	GatewayCertificateStore selfSignedStore,
	IOptionsMonitor<ActiveSyncOptions> options)
{
	/// <summary>
	///   Loads the certificate to serve. External paths are loaded from disk and any failure
	///   throws (a fail-fast startup error is better than silently serving an unexpected
	///   self-signed certificate); with no external path the self-signed one is generated if
	///   absent. Only called when <c>Tls.Enabled</c> is true.
	/// </summary>
	public async Task<(X509Certificate2 Certificate, TlsCertificateSource Source)> LoadForServingAsync(
		ILogger logger, CancellationToken ct)
	{
		TlsOptions tls = options.CurrentValue.Tls;
		if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
		{
			// K14: load the master key into a local we can zero, and surface (don't discard) a
			// loader error — a sealed CertificatePassword will otherwise fail with a misleading
			// "cannot be unsealed" message when the real cause is a misconfigured key.
			byte[]? masterKey = LoadMasterKey(logger);
			try
			{
				return (LoadExternal(tls, masterKey), TlsCertificateSource.External);
			}
			finally
			{
				if (masterKey is not null)
					CryptographicOperations.ZeroMemory(masterKey);
			}
		}

		X509Certificate2 selfSigned = await selfSignedStore.GetOrCreateAsync(
			GatewayCertificateStore.HostFromPublicUrl(options.CurrentValue.PublicUrl), logger, ct)
			.ConfigureAwait(false);
		return (selfSigned, TlsCertificateSource.SelfSigned);
	}

	/// <summary>
	///   Describes the active certificate without serving or generating anything — for the admin
	///   TLS panel and <c>eas tls</c>. Never returns a private key; an unreadable external file or
	///   a not-yet-generated self-signed certificate is reported via <see cref="TlsCertificateInfo.Error" />.
	/// </summary>
	public async Task<TlsCertificateInfo> DescribeAsync(ILogger logger, CancellationToken ct)
	{
		TlsOptions tls = options.CurrentValue.Tls;
		if (!tls.Enabled)
			return Empty(tls, TlsCertificateSource.Disabled, null, null);

		if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
		{
			byte[]? masterKey = LoadMasterKey(logger);
			try
			{
				using X509Certificate2 external = LoadExternal(tls, masterKey);
				return Describe(external, tls, TlsCertificateSource.External, tls.CertificatePath);
			}
			catch (Exception ex) when (ex is CryptographicException or IOException or
				                           UnauthorizedAccessException or InvalidOperationException)
			{
				return Empty(tls, TlsCertificateSource.External, tls.CertificatePath,
					$"Certificate at '{tls.CertificatePath}' cannot be loaded: {ex.Message}");
			}
			finally
			{
				if (masterKey is not null)
					CryptographicOperations.ZeroMemory(masterKey);
			}
		}

		X509Certificate2? stored = await selfSignedStore.TryLoadStoredAsync(logger, ct).ConfigureAwait(false);
		if (stored is null)
			return Empty(tls, TlsCertificateSource.SelfSigned, null,
				"No self-signed certificate stored yet — it is generated on the first HTTPS serve.");
		using (stored)
			return Describe(stored, tls, TlsCertificateSource.SelfSigned, null);
	}

	/// <summary>
	///   Loads the encryption master key, logging (rather than discarding) a loader error so a
	///   misconfigured key surfaces in diagnostics instead of hiding behind a downstream
	///   "cannot be unsealed" message (K14). The caller owns zeroing the returned buffer.
	/// </summary>
	private byte[]? LoadMasterKey(ILogger logger)
	{
		byte[]? key = EncryptionKeyLoader.TryLoadKey(options.CurrentValue.Encryption, out string? error);
		if (error is not null)
			logger.LogWarning(
				"ActiveSync:Encryption key could not be loaded ({Error}); a sealed Tls:CertificatePassword " +
				"cannot be unsealed.", error);
		return key;
	}

	/// <summary>Loads an operator-supplied certificate: a PEM cert+key pair, or a PFX bundle.</summary>
	internal static X509Certificate2 LoadExternal(TlsOptions tls, byte[]? masterKey)
	{
		string certPath = tls.CertificatePath!;
		string? password = ResolvePassword(tls.CertificatePassword, masterKey);

		if (!string.IsNullOrWhiteSpace(tls.CertificateKeyPath))
		{
			// PEM certificate + private-key pair. Re-export through PKCS#12 so the private key is
			// usable for TLS server auth on every platform (a cert straight from CreateFromPemFile
			// is not directly usable by Kestrel on Windows).
			using X509Certificate2 pem = string.IsNullOrEmpty(password)
				? X509Certificate2.CreateFromPemFile(certPath, tls.CertificateKeyPath)
				: X509Certificate2.CreateFromEncryptedPemFile(certPath, password, tls.CertificateKeyPath);
			return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
		}

		// PKCS#12 / PFX bundle (certificate + key in one file), optionally password-protected.
		return X509CertificateLoader.LoadPkcs12FromFile(certPath, password);
	}

	private static string? ResolvePassword(string? configured, byte[]? masterKey)
	{
		if (string.IsNullOrEmpty(configured))
			return null;
		if (!SecretValue.IsSealed(configured))
			return configured;
		if (masterKey is not null && SecretValue.TryUnseal(configured, masterKey, out string? plain, out _))
			return plain;
		throw new InvalidOperationException(
			"ActiveSync:Tls:CertificatePassword is sealed but cannot be unsealed with the current Encryption key.");
	}

	private static TlsCertificateInfo Empty(
		TlsOptions tls, TlsCertificateSource source, string? path, string? error) =>
		new(tls.Enabled, tls.Port, source, path, null, null, [], null, null, null, null, null, error);

	private static TlsCertificateInfo Describe(
		X509Certificate2 cert, TlsOptions tls, TlsCertificateSource source, string? path)
	{
		List<string> sans = [];
		X509Extension? sanExt = cert.Extensions["2.5.29.17"];
		if (sanExt is not null)
		{
			try
			{
				X509SubjectAlternativeNameExtension parsed = new(sanExt.RawData);
				sans.AddRange(parsed.EnumerateDnsNames());
				sans.AddRange(parsed.EnumerateIPAddresses().Select(ip => ip.ToString()));
			}
			catch (CryptographicException)
			{
				// Malformed SAN — leave the list empty rather than fail the whole description.
			}
		}

		int? keySize = cert.GetRSAPublicKey()?.KeySize
		               ?? cert.GetECDsaPublicKey()?.KeySize
		               ?? cert.GetDSAPublicKey()?.KeySize;

		return new TlsCertificateInfo(
			tls.Enabled, tls.Port, source, path,
			cert.Subject, cert.Issuer, sans,
			cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(),
			GatewayCertificateStore.Fingerprint(cert),
			new Oid(cert.GetKeyAlgorithm()).FriendlyName ?? cert.GetKeyAlgorithm(),
			keySize, null);
	}
}
