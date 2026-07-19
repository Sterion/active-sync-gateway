using System.Xml.Linq;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.WebUi.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   The DataProtection key repository over the state database: round-trip, sealed-at-rest
///   when the master key exists, plaintext fallback, and unreadable-row resilience.
/// </summary>
public sealed class DbXmlRepositoryTests : IDisposable
{
	private const string KeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

	private readonly SqliteConnection _connection;

	public DbXmlRepositoryTests()
	{
		// Shared in-memory database: alive while this connection is open.
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		using SyncDbContext db = CreateContext();
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private SqliteSyncDbContext CreateContext()
	{
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection)
			.Options;
		return new SqliteSyncDbContext(options);
	}

	private DbXmlRepository CreateRepository(string? encryptionKey)
	{
		ActiveSyncOptions options = new()
		{
			Encryption = encryptionKey is null
				? new EncryptionOptions { AllowPlaintext = true }
				: new EncryptionOptions { Key = encryptionKey }
		};
		return new DbXmlRepository(new ConnectionFactory(_connection), Options.Create(options));
	}

	[Fact]
	public void RoundTrip_SealedAtRest_WithMasterKey()
	{
		DbXmlRepository repository = CreateRepository(KeyBase64);
		XElement element = new("key", new XAttribute("id", "abc"), new XElement("value", "secret-material"));
		repository.StoreElement(element, "key-abc");

		// At rest: sealed, never the raw XML.
		using (SyncDbContext db = CreateContext())
		{
			DataProtectionKeyEntry row = Assert.Single(db.DataProtectionKeys.ToList());
			Assert.StartsWith("enc:v1:", row.Xml);
			Assert.DoesNotContain("secret-material", row.Xml);
		}

		XElement restored = Assert.Single(repository.GetAllElements());
		Assert.Equal("abc", restored.Attribute("id")?.Value);
		Assert.Equal("secret-material", restored.Element("value")?.Value);
	}

	[Fact]
	public void RoundTrip_Plaintext_WithoutMasterKey()
	{
		DbXmlRepository repository = CreateRepository(null);
		repository.StoreElement(new XElement("key", new XAttribute("id", "p1")), "key-p1");

		using (SyncDbContext db = CreateContext())
			Assert.DoesNotContain("enc:v1:", Assert.Single(db.DataProtectionKeys.ToList()).Xml);

		Assert.Single(repository.GetAllElements());
	}

	[Fact]
	public void UnreadableRows_AreSkipped_NotFatal()
	{
		// A sealed row without the master key (or under a rotated key) must not take the web
		// UI down — DataProtection just mints a fresh key.
		DbXmlRepository sealer = CreateRepository(KeyBase64);
		sealer.StoreElement(new XElement("key", new XAttribute("id", "s1")), "key-s1");

		DbXmlRepository keyless = CreateRepository(null);
		Assert.Empty(keyless.GetAllElements());

		// A second repository with the right key still reads it.
		Assert.Single(CreateRepository(KeyBase64).GetAllElements());
	}

	private sealed class ConnectionFactory(SqliteConnection connection) : ISyncDbContextFactory
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
