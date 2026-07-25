using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The self-signed gateway certificate: generated once, sealed with the encryption key,
///   reloaded identically on every later start, and replaced (not crashed on) when the
///   stored blob can no longer be read.
/// </summary>
public sealed class GatewayCertificateStoreTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;

	public GatewayCertificateStoreTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private static LocalContentProtector Protector(byte seed = 1)
	{
		byte[] key = new byte[32];
		Array.Fill(key, seed);
		return LocalContentProtector.CreateProtected(key);
	}

	private GatewayCertificateStore Store(LocalContentProtector protector)
	{
		return new GatewayCertificateStore(_factory, protector);
	}

	[Fact]
	public async Task GetOrCreate_PersistsOnce_AndReloadsTheSameCertificate()
	{
		using LocalContentProtector protector = Protector();
		GatewayCertificateStore store = Store(protector);

		using X509Certificate2 first = await store.GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);
		using X509Certificate2 second = await store.GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		Assert.Equal(first.Thumbprint, second.Thumbprint);
		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Equal(1, await db.ServerCertificates.CountAsync());
	}

	[Fact]
	public async Task GeneratedCertificate_HasServerShape()
	{
		// K4 BEHAVIOUR CHANGE: validity was historically 20 years (asserted here); it is now
		// capped at ~397 days (see GeneratedCertificate_ValidityIsCappedForAppleCompatibility)
		// with self-renewal ahead of expiry (see NearExpiryCertificate_IsRegeneratedAheadOfExpiry).
		using LocalContentProtector protector = Protector();
		using X509Certificate2 certificate = await Store(protector).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		Assert.True(certificate.HasPrivateKey);
		Assert.Equal("CN=eas.example.com", certificate.Subject);

		string san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
			.Single().Format(false);
		Assert.Contains("eas.example.com", san);
		Assert.Contains("localhost", san);
		X509EnhancedKeyUsageExtension eku =
			certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
		Assert.Contains("1.3.6.1.5.5.7.3.1", eku.EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value));
	}

	[Fact]
	public async Task GeneratedCertificate_IpHost_GetsAnIpSubjectAlternativeName()
	{
		// K5: SANs were DNS-only, so an IP-addressed client (the common Docker/k8s NodePort
		// case, or a phone pointed at a bare IP) got RemoteCertificateNameMismatch even though
		// the certificate was generated "for" that IP.
		using LocalContentProtector protector = Protector();
		using X509Certificate2 certificate = await Store(protector).GetOrCreateAsync(
			"203.0.113.5", NullLogger.Instance, CancellationToken.None);

		X509SubjectAlternativeNameExtension san =
			certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
		Assert.Contains("203.0.113.5", san.EnumerateIPAddresses().Select(ip => ip.ToString()));
	}

	[Fact]
	public async Task StoredBlob_IsSealedWithTheEncryptionKey()
	{
		using LocalContentProtector protector = Protector();
		using X509Certificate2 _ = await Store(protector).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		await using SyncDbContext db = _factory.CreateDbContext();
		ServerCertificate row = await db.ServerCertificates.SingleAsync();
		// LocalContentProtector's versioned ciphertext format — not a raw base64 PKCS#12
		// (those start with "MII", the DER SEQUENCE header).
		Assert.StartsWith("v1:", row.PfxProtected);
	}

	[Fact]
	public async Task UnreadableRow_IsReplacedInsteadOfCrashing()
	{
		using LocalContentProtector oldKey = Protector(1);
		using X509Certificate2 original = await Store(oldKey).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		// Same database, different encryption key: the blob no longer unseals. A dead HTTPS
		// endpoint would be worse than a fingerprint change, so a fresh cert must appear.
		using LocalContentProtector newKey = Protector(2);
		using X509Certificate2 replacement = await Store(newKey).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		Assert.NotEqual(original.Thumbprint, replacement.Thumbprint);
		using X509Certificate2 reloaded = await Store(newKey).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);
		Assert.Equal(replacement.Thumbprint, reloaded.Thumbprint);
	}

	[Fact]
	public async Task ConcurrentReplace_OfAnUnreadableRow_IsDetectedAsAConflict()
	{
		// K6: GetOrCreateAsync's "unreadable row -> replace" path had no concurrency guard, so
		// two replicas racing to replace the same unreadable row could both silently succeed —
		// each overwriting the other with no exception raised, flip-flopping the served
		// fingerprint on restart (indistinguishable from an active MITM to a device). This
		// reproduces the race at the row/DbContext level: two contexts read the same starting
		// row, the first replaces it, and the second's write — still based on the stale read —
		// must now be rejected as a conflict rather than silently applied.
		await using (SyncDbContext seed = _factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			seed.ServerCertificates.Add(new ServerCertificate
			{
				Id = 1, PfxProtected = "garbage-unreadable-blob", CreatedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
			await seed.SaveChangesAsync();
		}

		await using SyncDbContext replicaA = _factory.CreateDbContext();
		await using SyncDbContext replicaB = _factory.CreateDbContext();
		ServerCertificate rowA = await replicaA.ServerCertificates.FirstAsync(c => c.Id == 1);
		ServerCertificate rowB = await replicaB.ServerCertificates.FirstAsync(c => c.Id == 1);

		rowA.PfxProtected = "replica-a-replacement";
		await replicaA.SaveChangesAsync(); // first replica wins the race.

		rowB.PfxProtected = "replica-b-replacement";
		await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => replicaB.SaveChangesAsync());
	}

	[Fact]
	public async Task GeneratedCertificate_ValidityIsCappedForAppleCompatibility()
	{
		// K4: iOS/macOS refuse server certs valid for more than 398 days (Apple's ≤825/≤398-day
		// rule); the historical 20-year validity is hard-refused by the primary EAS client.
		using LocalContentProtector protector = Protector();
		using X509Certificate2 certificate = await Store(protector).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		TimeSpan validity = certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime();
		Assert.True(validity.TotalDays <= 398,
			$"Certificate validity of {validity.TotalDays:F1} days exceeds Apple's 398-day limit");
	}

	[Fact]
	public async Task NearExpiryCertificate_IsRegeneratedAheadOfExpiry()
	{
		// K4: with validity now capped well under a year, a certificate that is never renewed
		// would eventually be refused by clients (or expire outright). GetOrCreateAsync must
		// notice a stored certificate is close to its NotAfter and regenerate ahead of time,
		// the same way it already replaces an unreadable row.
		using LocalContentProtector protector = Protector();
		const string host = "eas.example.com";

		// Seed a row directly with a certificate that expires in 5 days — bypassing
		// GatewayCertificateStore.Generate (which enforces the capped validity itself), so the
		// seeded blob's expiry is fully under this test's control.
		using RSA key = RSA.Create(2048);
		CertificateRequest request = new($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		using X509Certificate2 expiringSoon = request.CreateSelfSigned(now.AddDays(-390), now.AddDays(5));
		string sealedPfx = protector.Protect(
			Convert.ToBase64String(expiringSoon.Export(X509ContentType.Pkcs12)), "_gateway", "tls");
		await using (SyncDbContext seed = _factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			seed.ServerCertificates.Add(new ServerCertificate { Id = 1, PfxProtected = sealedPfx, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
			await seed.SaveChangesAsync();
		}

		using X509Certificate2 renewed = await Store(protector).GetOrCreateAsync(
			host, NullLogger.Instance, CancellationToken.None);

		Assert.NotEqual(expiringSoon.Thumbprint, renewed.Thumbprint);
		Assert.True(renewed.NotAfter.ToUniversalTime() > now.AddDays(300));
	}

	[Fact]
	public async Task GeneratedCertificate_PrivateKeyStaysUsable_AfterPfxIsZeroed()
	{
		// K9 COVERAGE (not proof): the fix zeroes the unencrypted PKCS#12 byte buffers after they
		// are loaded/sealed — there is no external handle to observe the wipe itself, so this test
		// is a regression guard that zeroing the buffer after LoadPkcs12 does not corrupt the loaded
		// private key. A sign/verify round-trip proves the key survived.
		using LocalContentProtector protector = Protector();
		using X509Certificate2 certificate = await Store(protector).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		Assert.True(certificate.HasPrivateKey);
		using RSA priv = certificate.GetRSAPrivateKey()!;
		using RSA pub = certificate.GetRSAPublicKey()!;
		byte[] data = [1, 2, 3, 4, 5];
		byte[] signature = priv.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		Assert.True(pub.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
	}

	[Fact]
	public async Task GeneratedCertificate_UnusableHost_FallsBackInsteadOfThrowing()
	{
		// K19: Generate could throw at first serve on an odd PublicUrl host with no fallback —
		// an unhandled-exception startup death instead of degrading to FallbackHost. This host is
		// not a valid IDN name, so SubjectAlternativeNameBuilder.AddDnsName throws ArgumentException.
		using LocalContentProtector protector = Protector();
		string unusableHost = "exa\u0000mple.com";

		using X509Certificate2 certificate = await Store(protector).GetOrCreateAsync(
			unusableHost, NullLogger.Instance, CancellationToken.None);

		Assert.Equal($"CN={GatewayCertificateStore.FallbackHost}", certificate.Subject);
	}

	[Theory]
	[InlineData(null, "activesync-gateway")]
	[InlineData("not a url", "activesync-gateway")]
	[InlineData("https://eas.example.com", "eas.example.com")]
	[InlineData("https://eas.example.com:8443/path", "eas.example.com")]
	public void HostFromPublicUrl_UsesTheHostOrFallsBack(string? publicUrl, string expected)
	{
		Assert.Equal(expected, GatewayCertificateStore.HostFromPublicUrl(publicUrl));
	}

	private sealed class TestContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext()
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection)
				.Options;
			return new SqliteSyncDbContext(options);
		}
	}
}
