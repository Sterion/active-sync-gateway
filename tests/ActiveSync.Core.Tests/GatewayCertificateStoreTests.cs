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
	public async Task GeneratedCertificate_HasServerShape_And20YearValidity()
	{
		using LocalContentProtector protector = Protector();
		using X509Certificate2 certificate = await Store(protector).GetOrCreateAsync(
			"eas.example.com", NullLogger.Instance, CancellationToken.None);

		Assert.True(certificate.HasPrivateKey);
		Assert.Equal("CN=eas.example.com", certificate.Subject);
		Assert.InRange(certificate.NotAfter.ToUniversalTime(),
			DateTime.UtcNow.AddYears(20).AddDays(-2), DateTime.UtcNow.AddYears(20).AddDays(2));

		string san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
			.Single().Format(false);
		Assert.Contains("eas.example.com", san);
		Assert.Contains("localhost", san);
		X509EnhancedKeyUsageExtension eku =
			certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
		Assert.Contains("1.3.6.1.5.5.7.3.1", eku.EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value));
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
