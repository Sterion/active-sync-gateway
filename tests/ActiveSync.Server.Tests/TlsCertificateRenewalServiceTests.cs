using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   K1: <see cref="GatewayCertificateStore.GetOrCreateAsync" /> already renews a self-signed
///   certificate ahead of expiry, but nothing ever called it again after the one startup call —
///   a long-lived process rode a 397-day-capped certificate straight past expiry with no operator
///   signal. <see cref="TlsCertificateRenewalService" /> is the missing periodic caller: it ticks,
///   asks the store whether renewal is due, and swaps the <see cref="CertificateHolder" /> instance
///   Kestrel's HTTPS selector reads.
/// </summary>
public sealed class TlsCertificateRenewalServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private const string Host = "eas.example.com";

	public TlsCertificateRenewalServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	private static LocalContentProtector Protector()
	{
		byte[] key = new byte[32];
		Array.Fill(key, (byte)9);
		return LocalContentProtector.CreateProtected(key);
	}

	/// <summary>Seeds a row whose certificate expires in 5 days, bypassing the store's own capped Generate.</summary>
	private async Task<X509Certificate2> SeedNearExpiryCertificateAsync(LocalContentProtector protector)
	{
		using RSA key = RSA.Create(2048);
		CertificateRequest request = new($"CN={Host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		X509Certificate2 expiringSoon = request.CreateSelfSigned(now.AddDays(-390), now.AddDays(5));
		string sealedPfx = protector.Protect(
			Convert.ToBase64String(expiringSoon.Export(X509ContentType.Pkcs12)),
			LocalContentProtector.GatewayUserId, "tls");
		await using SyncDbContext db = _factory.CreateDbContext();
#pragma warning disable VSTHRD103
		db.ServerCertificates.Add(new ServerCertificate { Id = 1, PfxProtected = sealedPfx, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();
		return expiringSoon;
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		while (!condition() && DateTime.UtcNow < deadline)
			await Task.Delay(20);
	}

	[Fact]
	public async Task NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped()
	{
		LocalContentProtector protector = Protector();
		X509Certificate2 stale = await SeedNearExpiryCertificateAsync(protector);
		GatewayCertificateStore store = new(_factory, protector);
		CertificateHolder holder = new() { Current = stale };

		// The service OWNS `stale` from the moment it swaps the holder: DisposeAfterGraceAsync frees the
		// previous certificate once the grace period elapses (20 ms here, 30 s in production). Reading
		// `stale.Thumbprint` after that point throws `CryptographicException: m_safeCertContext is an
		// invalid handle` — which is what made this test flaky under parallel load, since both the poll
		// predicate and the assertion below had to run inside a 20 ms window to win the race. Capture the
		// thumbprint as a string BEFORE the service can start, so nothing here touches a handle the
		// service is entitled to close. This is a test-lifetime fix only: the production race is what the
		// grace period exists to bound, and it is untouched.
		string staleThumbprint = stale.Thumbprint;

		ActiveSyncOptions options = new();
		TlsCertificateRenewalService service = new(
			holder, store, TestOptionsMonitor.Of(options), NullLogger<TlsCertificateRenewalService>.Instance,
			tickInterval: TimeSpan.FromMilliseconds(20), disposeGracePeriod: TimeSpan.FromMilliseconds(20));

		await service.StartAsync(CancellationToken.None);
		try
		{
			// Generous, not slow: the wait returns the moment the swap is observed, so the ceiling only
			// matters on a machine where RSA-2048 generation plus scheduler jitter runs long under load.
			await WaitUntilAsync(() => holder.Current is { } cur && cur.Thumbprint != staleThumbprint,
				TimeSpan.FromSeconds(30));

			X509Certificate2? renewed = holder.Current;
			Assert.NotNull(renewed);
			Assert.NotEqual(staleThumbprint, renewed!.Thumbprint);
			Assert.True(renewed.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(300));
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task ExternalCertificateConfigured_TheServiceNeverTouchesTheStoreOrTheHolder()
	{
		LocalContentProtector protector = Protector();
		GatewayCertificateStore store = new(_factory, protector);
		CertificateHolder holder = new();

		ActiveSyncOptions options = new();
		options.Tls.CertificatePath = "/some/mounted/cert.pfx";
		TlsCertificateRenewalService service = new(
			holder, store, TestOptionsMonitor.Of(options), NullLogger<TlsCertificateRenewalService>.Instance,
			tickInterval: TimeSpan.FromMilliseconds(20), disposeGracePeriod: TimeSpan.FromMilliseconds(20));

		await service.StartAsync(CancellationToken.None);
		try
		{
			await Task.Delay(300);
			Assert.Null(holder.Current);
			await using SyncDbContext db = _factory.CreateDbContext();
			Assert.Equal(0, await db.ServerCertificates.CountAsync());
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task TlsDisabled_TheServiceNeverTouchesTheStoreOrTheHolder()
	{
		LocalContentProtector protector = Protector();
		GatewayCertificateStore store = new(_factory, protector);
		CertificateHolder holder = new();

		ActiveSyncOptions options = new();
		options.Tls.Enabled = false;
		TlsCertificateRenewalService service = new(
			holder, store, TestOptionsMonitor.Of(options), NullLogger<TlsCertificateRenewalService>.Instance,
			tickInterval: TimeSpan.FromMilliseconds(20), disposeGracePeriod: TimeSpan.FromMilliseconds(20));

		await service.StartAsync(CancellationToken.None);
		try
		{
			await Task.Delay(300);
			Assert.Null(holder.Current);
			await using SyncDbContext db = _factory.CreateDbContext();
			Assert.Equal(0, await db.ServerCertificates.CountAsync());
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
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
