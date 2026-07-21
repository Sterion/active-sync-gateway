using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Security;

/// <summary>
///   Loads — or generates on first serve — the gateway's self-signed TLS certificate. The
///   PKCS#12 blob lives as a single well-known row in the state database (base64, sealed with
///   the Encryption master key via <see cref="LocalContentProtector" />), so every restart and
///   every replica serves the same certificate and devices only have to trust it once. The
///   20-year validity deliberately outlives any deployment: there is no renewal logic —
///   deleting the row is the (never normally needed) regeneration lever.
/// </summary>
public sealed class GatewayCertificateStore(ISyncDbContextFactory contextFactory, LocalContentProtector protector)
{
	/// <summary>AAD binding for the sealed blob — no user owns the gateway certificate.</summary>
	private const string AadUser = "_gateway";
	private const string AadCollection = "tls";

	/// <summary>Subject/SAN host used when no <c>PublicUrl</c> is configured.</summary>
	public const string FallbackHost = "activesync-gateway";

	/// <summary>The DNS host to certify: the PublicUrl's host when set, else a fixed name.</summary>
	public static string HostFromPublicUrl(string? publicUrl)
	{
		return Uri.TryCreate(publicUrl, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host)
			? uri.Host
			: FallbackHost;
	}

	/// <summary>
	///   Returns the stored self-signed certificate without generating one, for read-only
	///   inspection (the TLS details view / <c>eas tls</c>). Null when no row exists yet or it
	///   cannot be decrypted.
	/// </summary>
	public async Task<X509Certificate2?> TryLoadStoredAsync(ILogger logger, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		ServerCertificate? row = await db.ServerCertificates.AsNoTracking()
			.FirstOrDefaultAsync(c => c.Id == 1, ct).ConfigureAwait(false);
		return row is null ? null : TryLoad(row.PfxProtected, logger);
	}

	public async Task<X509Certificate2> GetOrCreateAsync(string host, ILogger logger, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		ServerCertificate? row = await db.ServerCertificates
			.FirstOrDefaultAsync(c => c.Id == 1, ct).ConfigureAwait(false);

		if (row is not null)
		{
			X509Certificate2? stored = TryLoad(row.PfxProtected, logger);
			if (stored is not null)
				return stored;
			// Unreadable row (encryption key changed, tampering): a dead HTTPS endpoint would
			// be worse than a fingerprint change, so fall through and replace it.
		}

		(X509Certificate2 certificate, string pfxProtected) = Generate(host);
		if (row is null)
		{
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.ServerCertificates.Add(row = new ServerCertificate { Id = 1, PfxProtected = pfxProtected });
#pragma warning restore VSTHRD103
		}
		else
			row.PfxProtected = pfxProtected;
		row.CreatedUtc = DateTime.UtcNow;
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			logger.LogInformation(
				"Generated self-signed TLS certificate for {Host} (SHA-256 {Fingerprint}), stored in the database",
				host, Fingerprint(certificate));
			return certificate;
		}
		catch (DbUpdateException)
		{
			// Another replica won the first-boot insert race — serve the winner's certificate.
			certificate.Dispose();
			await using SyncDbContext retry = contextFactory.CreateDbContext();
			ServerCertificate winner = await retry.ServerCertificates.AsNoTracking()
				.FirstAsync(c => c.Id == 1, ct).ConfigureAwait(false);
			return TryLoad(winner.PfxProtected, logger)
			       ?? throw new InvalidOperationException(
				       "The concurrently stored gateway TLS certificate cannot be read back.");
		}
	}

	/// <summary>Colon-separated SHA-256 fingerprint, as certificate viewers on phones show it.</summary>
	public static string Fingerprint(X509Certificate2 certificate)
	{
		return string.Join(':', Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))
			.Chunk(2).Select(pair => new string(pair)));
	}

	private X509Certificate2? TryLoad(string pfxProtected, ILogger logger)
	{
		try
		{
			byte[] pfx = Convert.FromBase64String(protector.Unprotect(pfxProtected, AadUser, AadCollection));
			return X509CertificateLoader.LoadPkcs12(pfx, null);
		}
		catch (Exception ex) when (ex is BackendException or FormatException or CryptographicException)
		{
			logger.LogWarning(ex,
				"Stored gateway TLS certificate cannot be read (changed encryption key?) — generating a new one; " +
				"devices will have to trust the new fingerprint");
			return null;
		}
	}

	private (X509Certificate2 Certificate, string PfxProtected) Generate(string host)
	{
		using RSA key = RSA.Create(2048);
		CertificateRequest request = new(
			$"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(
			X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
		// serverAuth — the only purpose this certificate has.
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
			new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
		SubjectAlternativeNameBuilder san = new();
		san.AddDnsName(host);
		if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
			san.AddDnsName("localhost");
		request.CertificateExtensions.Add(san.Build());

		// Backdated an hour so a device with mild clock skew accepts it immediately.
		DateTimeOffset now = DateTimeOffset.UtcNow;
		using X509Certificate2 generated = request.CreateSelfSigned(now.AddHours(-1), now.AddYears(20));
		byte[] pfx = generated.Export(X509ContentType.Pkcs12);
		string sealedPfx = protector.Protect(Convert.ToBase64String(pfx), AadUser, AadCollection);
		return (X509CertificateLoader.LoadPkcs12(pfx, null), sealedPfx);
	}
}
