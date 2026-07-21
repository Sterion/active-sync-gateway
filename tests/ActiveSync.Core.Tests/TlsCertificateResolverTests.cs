using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
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
	public void LoadExternal_Pfx_HasPrivateKey()
	{
		TlsOptions tls = new() { CertificatePath = _pfx };
		using X509Certificate2 cert = TlsCertificateResolver.LoadExternal(tls, null);
		Assert.True(cert.HasPrivateKey);
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
