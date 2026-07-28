using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Operator-supplied HTTPS certificates (ActiveSync:Tls): loading a mounted PEM pair or PFX,
///   unsealing a sealed password, and describing the active certificate (external, self-signed,
///   disabled, or configured-but-unreadable) for the admin panel / <c>eas tls</c> — never
///   leaking a private key.
/// </summary>
public sealed class TlsCertificateResolverTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly string _dir;
	private readonly string _pemCert;
	private readonly string _pemKey;
	private readonly string _pfx;
	private readonly string _pfxWithPassword;
	private const string PfxPassword = "pfx-secret";

	public TlsCertificateResolverTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();

		_dir = Path.Combine(Path.GetTempPath(), "eas-tls-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_pemCert = Path.Combine(_dir, "fullchain.pem");
		_pemKey = Path.Combine(_dir, "privkey.pem");
		_pfx = Path.Combine(_dir, "cert.pfx");
		_pfxWithPassword = Path.Combine(_dir, "cert-pw.pfx");

		using X509Certificate2 cert = MakeCert();
		File.WriteAllText(_pemCert, cert.ExportCertificatePem());
		File.WriteAllText(_pemKey, cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
		File.WriteAllBytes(_pfx, cert.Export(X509ContentType.Pkcs12));
		File.WriteAllBytes(_pfxWithPassword, cert.Export(X509ContentType.Pkcs12, PfxPassword));
	}

	public void Dispose()
	{
		_connection.Dispose();
		try { Directory.Delete(_dir, true); }
		catch (IOException) { /* best effort */ }
	}

	private static X509Certificate2 MakeCert()
	{
		using RSA rsa = RSA.Create(2048);
		CertificateRequest request = new("CN=tls.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		SubjectAlternativeNameBuilder san = new();
		san.AddDnsName("tls.example.com");
		san.AddDnsName("www.example.com");
		request.CertificateExtensions.Add(san.Build());
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return request.CreateSelfSigned(now.AddHours(-1), now.AddYears(1));
	}

	private static byte[] Key() => Enumerable.Repeat((byte)7, 32).ToArray();

	private TlsCertificateResolver Resolver(ActiveSyncOptions options, out GatewayCertificateStore store)
	{
		store = new GatewayCertificateStore(_factory, LocalContentProtector.CreateProtected(Key()));
		return new TlsCertificateResolver(store, TestOptionsMonitor.Of(options));
	}

	[Fact]
	public void LoadExternal_PemPair_HasPrivateKey()
	{
		TlsOptions tls = new() { CertificatePath = _pemCert, CertificateKeyPath = _pemKey };
		using X509Certificate2 cert = TlsCertificateResolver.LoadExternal(tls, null);
		Assert.True(cert.HasPrivateKey);
		Assert.Equal("CN=tls.example.com", cert.Subject);
	}

	[Fact]
	public void LoadExternal_PemPair_PrivateKeyStaysUsable_AfterPfxIsZeroed()
	{
		// Coverage (not proof): the PEM path re-exports through PKCS#12 into an anonymous
		// temporary handed straight to LoadPkcs12 — unlike GatewayCertificateStore.Generate, which
		// hoists the identical export into a named local and zeroes it in a finally. There is no
		// external handle onto that anonymous buffer to assert it was wiped (the whole point of the
		// finding is that nothing holds a reference to zero); this is a regression guard, in the same
		// spirit as the analogous zero-after-load regression guard in GatewayCertificateStoreTests, that hoisting it into a zeroed local does
		// not corrupt the loaded private key. A sign/verify round-trip proves the key survived.
		TlsOptions tls = new() { CertificatePath = _pemCert, CertificateKeyPath = _pemKey };
		using X509Certificate2 cert = TlsCertificateResolver.LoadExternal(tls, null);

		using RSA priv = cert.GetRSAPrivateKey()!;
		using RSA pub = cert.GetRSAPublicKey()!;
		byte[] data = [1, 2, 3, 4, 5];
		byte[] signature = priv.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		Assert.True(pub.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
	}

	[Fact]
	public void LoadExternal_Pfx_HasPrivateKey()
	{
		TlsOptions tls = new() { CertificatePath = _pfx };
		using X509Certificate2 cert = TlsCertificateResolver.LoadExternal(tls, null);
		Assert.True(cert.HasPrivateKey);
	}

	[Fact]
	public void LoadExternal_KeylessCertificate_ThrowsInsteadOfLoadingSilently()
	{
		// A cert-only PFX (no private key) loaded "successfully" and Kestrel then failed
		// opaquely at handshake time — defeating README's documented "fails startup with a clear
		// error". LoadExternal must reject it itself.
		using X509Certificate2 fullCert = MakeCert();
		using X509Certificate2 certOnly = X509CertificateLoader.LoadCertificate(fullCert.Export(X509ContentType.Cert));
		string path = Path.Combine(_dir, "keyless.pfx");
		File.WriteAllBytes(path, certOnly.Export(X509ContentType.Pkcs12));

		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			TlsCertificateResolver.LoadExternal(new TlsOptions { CertificatePath = path }, null));
		Assert.Contains("private key", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void LoadExternal_ExpiredCertificate_ThrowsInsteadOfLoadingSilently()
	{
		// An already-expired operator cert loaded "successfully" too, so the failure only
		// surfaced later (and opaquely) at the TLS handshake.
		using RSA rsa = RSA.Create(2048);
		CertificateRequest request = new(
			"CN=expired.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		using X509Certificate2 expired = request.CreateSelfSigned(now.AddDays(-30), now.AddDays(-1));
		string path = Path.Combine(_dir, "expired.pfx");
		File.WriteAllBytes(path, expired.Export(X509ContentType.Pkcs12));

		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			TlsCertificateResolver.LoadExternal(new TlsOptions { CertificatePath = path }, null));
		Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void LoadExternal_NotYetValidCertificate_ThrowsInsteadOfLoadingSilently()
	{
		// A certificate whose NotBefore is in the future (a raced ACME issuance, a pre-staged
		// rotation mount, clock skew) passed both existing checks and loaded "successfully" — the
		// mirror-image case of the expired-certificate guard above, and just as opaque at handshake time.
		using RSA rsa = RSA.Create(2048);
		CertificateRequest request = new(
			"CN=not-yet-valid.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		using X509Certificate2 notYetValid = request.CreateSelfSigned(now.AddDays(1), now.AddDays(30));
		string path = Path.Combine(_dir, "not-yet-valid.pfx");
		File.WriteAllBytes(path, notYetValid.Export(X509ContentType.Pkcs12));

		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			TlsCertificateResolver.LoadExternal(new TlsOptions { CertificatePath = path }, null));
		Assert.Contains("not valid until", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void LoadExternal_PfxWithPassword_Loads_AndRejectsWrongPassword()
	{
		using X509Certificate2 ok = TlsCertificateResolver.LoadExternal(
			new TlsOptions { CertificatePath = _pfxWithPassword, CertificatePassword = PfxPassword }, null);
		Assert.True(ok.HasPrivateKey);

		Assert.Throws<CryptographicException>(() => TlsCertificateResolver.LoadExternal(
			new TlsOptions { CertificatePath = _pfxWithPassword, CertificatePassword = "wrong" }, null));
	}

	[Fact]
	public void LoadExternal_SealedPassword_IsUnsealed()
	{
		byte[] key = Key();
		string sealedPw = SecretValue.Seal(PfxPassword, key);
		Assert.True(SecretValue.IsSealed(sealedPw));

		using X509Certificate2 cert = TlsCertificateResolver.LoadExternal(
			new TlsOptions { CertificatePath = _pfxWithPassword, CertificatePassword = sealedPw }, key);
		Assert.True(cert.HasPrivateKey);
	}

	[Fact]
	public async Task Describe_External_CalledRepeatedly_KeepsReturningCorrectKeySize()
	{
		// Coverage (not proof): Describe's KeySize read via GetRSAPublicKey()/
		// GetECDsaPublicKey()/GetDSAPublicKey() leaked the returned AsymmetricAlgorithm's native
		// handle (none were disposed) — reachable from the admin TLS panel / `eas tls`, both of
		// which can poll it repeatedly. A leaked native handle has no directly observable symptom
		// in a unit test (it shows up as accumulated OS handles under sustained polling, not a
		// value a single call can assert on); this guards that reading KeySize through a
		// dispose-after-use pattern still returns the correct value across repeated calls.
		ActiveSyncOptions options = new() { Encryption = new EncryptionOptions { AllowPlaintext = true } };
		options.Tls.CertificatePath = _pemCert;
		options.Tls.CertificateKeyPath = _pemKey;
		TlsCertificateResolver resolver = Resolver(options, out _);

		for (int i = 0; i < 25; i++)
		{
			TlsCertificateInfo info = await resolver.DescribeAsync(NullLogger.Instance, CancellationToken.None);
			Assert.Equal("RSA", info.KeyAlgorithm);
			Assert.Equal(2048, info.KeySize);
		}
	}

	[Fact]
	public async Task Describe_External_ReturnsDetails_NoError()
	{
		ActiveSyncOptions options = new()
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true },
		};
		options.Tls.CertificatePath = _pemCert;
		options.Tls.CertificateKeyPath = _pemKey;

		TlsCertificateInfo info = await Resolver(options, out _)
			.DescribeAsync(NullLogger.Instance, CancellationToken.None);

		Assert.Equal(TlsCertificateSource.External, info.Source);
		Assert.Equal(_pemCert, info.CertificatePath);
		Assert.Equal("CN=tls.example.com", info.Subject);
		Assert.Contains("tls.example.com", info.SubjectAlternativeNames);
		Assert.Contains("www.example.com", info.SubjectAlternativeNames);
		Assert.NotNull(info.Fingerprint);
		Assert.Equal("RSA", info.KeyAlgorithm);
		Assert.Equal(2048, info.KeySize);
		Assert.Null(info.Error);
	}

	[Fact]
	public async Task Describe_External_SealedPassword_UnsealsWithMasterKey_NoError()
	{
		// Coverage (not proof): the resolver now loads the master key into a buffer it zeroes
		// after use and logs (rather than discards) a loader error. The wipe itself has no external
		// handle; this test guards that the key-lifetime refactor still unseals a sealed
		// CertificatePassword correctly (load -> unseal -> zero, all functional).
		string sealedPw = SecretValue.Seal(PfxPassword, Key());
		ActiveSyncOptions options = new()
		{
			Encryption = new EncryptionOptions { Key = Convert.ToBase64String(Key()) }
		};
		options.Tls.CertificatePath = _pfxWithPassword;
		options.Tls.CertificatePassword = sealedPw;

		TlsCertificateInfo info = await Resolver(options, out _)
			.DescribeAsync(NullLogger.Instance, CancellationToken.None);

		Assert.Equal(TlsCertificateSource.External, info.Source);
		Assert.Null(info.Error);
		Assert.NotNull(info.Fingerprint);
	}

	[Fact]
	public async Task Describe_External_MissingFile_ReportsErrorInsteadOfThrowing()
	{
		ActiveSyncOptions options = new() { Encryption = new EncryptionOptions { AllowPlaintext = true } };
		options.Tls.CertificatePath = Path.Combine(_dir, "does-not-exist.pfx");

		TlsCertificateInfo info = await Resolver(options, out _)
			.DescribeAsync(NullLogger.Instance, CancellationToken.None);

		Assert.Equal(TlsCertificateSource.External, info.Source);
		Assert.NotNull(info.Error);
		Assert.Null(info.Fingerprint);
	}

	[Fact]
	public async Task Describe_Disabled_ReportsDisabled()
	{
		ActiveSyncOptions options = new();
		options.Tls.Enabled = false;

		TlsCertificateInfo info = await Resolver(options, out _)
			.DescribeAsync(NullLogger.Instance, CancellationToken.None);

		Assert.Equal(TlsCertificateSource.Disabled, info.Source);
	}

	[Fact]
	public async Task Describe_SelfSigned_UsesStoredCertificate()
	{
		ActiveSyncOptions options = new();
		TlsCertificateResolver resolver = Resolver(options, out GatewayCertificateStore store);

		// Seed the stored self-signed certificate the way the first serve would.
		using X509Certificate2 seeded = await store.GetOrCreateAsync(
			"activesync-gateway", NullLogger.Instance, CancellationToken.None);

		TlsCertificateInfo info = await resolver.DescribeAsync(NullLogger.Instance, CancellationToken.None);
		Assert.Equal(TlsCertificateSource.SelfSigned, info.Source);
		Assert.Equal("CN=activesync-gateway", info.Subject);
		Assert.Equal(GatewayCertificateStore.Fingerprint(seeded), info.Fingerprint);
		Assert.Null(info.Error);
	}

	[Fact]
	public async Task Describe_SelfSigned_NotGeneratedYet_ReportsPending()
	{
		ActiveSyncOptions options = new();
		TlsCertificateInfo info = await Resolver(options, out _)
			.DescribeAsync(NullLogger.Instance, CancellationToken.None);

		Assert.Equal(TlsCertificateSource.SelfSigned, info.Source);
		Assert.Null(info.Fingerprint);
		Assert.NotNull(info.Error);
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
