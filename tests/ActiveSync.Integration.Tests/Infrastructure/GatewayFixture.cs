using ActiveSync.Core.Options;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MimeKit;
using Npgsql;

namespace ActiveSync.Integration.Tests.Infrastructure;

/// <summary>
///   Hosts the gateway in-process (TestServer) against the real backend stack. One shared
///   instance per test collection; a second, read-only instance is created on demand.
/// </summary>
public sealed class GatewayFixture : IAsyncLifetime
{
	/// <summary>Fixed 256-bit local-content encryption key (base64 of bytes 0..31).</summary>
	public const string TestEncryptionKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

	private readonly List<string> _tempDbs = [];
	private string _localStoresConnectionString = "";
	private bool _localStoresIsPostgres;
	private WebApplicationFactory<Program>? _factory;
	private WebApplicationFactory<Program>? _localStoresFactory;
	private WebApplicationFactory<Program>? _readOnlyFactory;
	private WebApplicationFactory<Program>? _watchdogFactory;
	private WebApplicationFactory<Program>? _jmapFactory;

	public WebApplicationFactory<Program> Factory =>
		_factory ??= CreateFactory(false);

	/// <summary>
	///   Mail served over JMAP (MailStore + MailSubmit → the "jmap" provider against Stalwart's
	///   HTTP listener); calendar/contacts stay on CalDAV/CardDAV. Proves the JMAP mail path
	///   end-to-end.
	/// </summary>
	public WebApplicationFactory<Program> JmapFactory =>
		_jmapFactory ??= CreateFactory(false, jmap: true);

	public WebApplicationFactory<Program> ReadOnlyFactory =>
		_readOnlyFactory ??= CreateFactory(true);

	/// <summary>
	///   IDLE disabled + a fast 15 s watchdog, so tests can prove the exact re-check catches
	///   changes the IDLE/STATUS watchers never report.
	/// </summary>
	public WebApplicationFactory<Program> WatchdogFactory =>
		_watchdogFactory ??= CreateFactory(false, true);

	/// <summary>
	///   Mail-only gateway (no CalDAV/CardDAV even when the stack has DAV): contacts,
	///   calendar and notes are served from the gateway's own database (local stores).
	/// </summary>
	public WebApplicationFactory<Program> LocalStoresFactory =>
		_localStoresFactory ??= CreateFactory(false, withoutDav: true);

	/// <summary>
	///   Raw ADO read of LocalItems.Content behind <see cref="LocalStoresFactory" /> —
	///   deliberately bypasses the EF/store layer so at-rest assertions see exactly the
	///   stored bytes, on whichever provider the suite runs against.
	/// </summary>
	public List<string> ReadLocalItemContents(string userName, string collection)
	{
		List<string> rows = new();
		if (_localStoresIsPostgres)
		{
			PostgresConnectionUri.TryConvert(_localStoresConnectionString, out string keywordForm, out _);
			using NpgsqlConnection connection = new(keywordForm);
			connection.Open();
			using NpgsqlCommand command = connection.CreateCommand();
			command.CommandText =
				"SELECT \"Content\" FROM \"LocalItems\" WHERE \"UserName\" = @user AND \"Collection\" = @collection";
			command.Parameters.AddWithValue("user", userName);
			command.Parameters.AddWithValue("collection", collection);
			using NpgsqlDataReader reader = command.ExecuteReader();
			while (reader.Read())
				rows.Add(reader.GetString(0));
			return rows;
		}

		using SqliteConnection sqlite = new($"{_localStoresConnectionString};Mode=ReadOnly");
		sqlite.Open();
		using SqliteCommand sqliteCommand = sqlite.CreateCommand();
		sqliteCommand.CommandText =
			"SELECT Content FROM LocalItems WHERE UserName = $user AND Collection = $collection";
		sqliteCommand.Parameters.AddWithValue("$user", userName);
		sqliteCommand.Parameters.AddWithValue("$collection", collection);
		using SqliteDataReader sqliteReader = sqliteCommand.ExecuteReader();
		while (sqliteReader.Read())
			rows.Add(sqliteReader.GetString(0));
		return rows;
	}

	public async Task InitializeAsync()
	{
		// The warm-up uses plain SMTP submission; skip it on backends with no submission MSA
		// (Cyrus is LMTP-only and sends over JMAP instead — its tests poll for delivery).
		if (!TestBackend.IsAvailable || !TestBackend.SmtpSubmissionAvailable)
			return;
		// A cold backend intermittently stalls the FIRST delivery into each mailbox for
		// over a minute (observed on Stalwart), which flaked exactly one test per fresh
		// stack — every CI run is a fresh stack. Warm both directions once, before any
		// test runs.
		await WarmDeliveryAsync(TestBackend.User1, TestBackend.User2);
		await WarmDeliveryAsync(TestBackend.User2, TestBackend.User1);
	}

	public async Task DisposeAsync()
	{
		if (_factory is not null)
			await _factory.DisposeAsync();
		if (_readOnlyFactory is not null)
			await _readOnlyFactory.DisposeAsync();
		if (_watchdogFactory is not null)
			await _watchdogFactory.DisposeAsync();
		if (_localStoresFactory is not null)
			await _localStoresFactory.DisposeAsync();
		if (_jmapFactory is not null)
			await _jmapFactory.DisposeAsync();
		foreach (string db in _tempDbs)
			try
			{
				File.Delete(db);
			}
			catch (IOException)
			{
				// still locked on Windows — temp files get cleaned eventually
			}
	}

	public HttpClient CreateHttpClient(bool readOnly = false)
	{
		return (readOnly ? ReadOnlyFactory : Factory).CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false
		});
	}

	public EasTestClient CreateEasClient(string user, string? deviceId = null, bool readOnly = false)
	{
		return new EasTestClient(CreateHttpClient(readOnly), user, TestBackend.Password,
			deviceId ?? $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
	}

	public EasTestClient CreateWatchdogEasClient(string user)
	{
		return new EasTestClient(
			WatchdogFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			user, TestBackend.Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
	}

	public EasTestClient CreateLocalStoresEasClient(string user)
	{
		return new EasTestClient(
			LocalStoresFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			user, TestBackend.Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
	}

	public EasTestClient CreateJmapEasClient(string user)
	{
		return new EasTestClient(
			JmapFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			user, TestBackend.Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
	}

	/// <summary>
	///   A private gateway instance the caller owns and disposes — for tests that stop the
	///   host (shutdown behavior) or need special settings, without touching the shared
	///   factories.
	/// </summary>
	public WebApplicationFactory<Program> CreateIsolatedFactory(
		Dictionary<string, string?>? overrides = null)
	{
		return CreateFactory(false, overrides: overrides);
	}

	private WebApplicationFactory<Program> CreateFactory(
		bool readOnly, bool fastWatchdogNoIdle = false, bool withoutDav = false,
		Dictionary<string, string?>? overrides = null, bool jmap = false)
	{
		string connectionString;
		if (TestBackend.PostgresUri is { } adminUri)
		{
			// One throwaway database per gateway factory. The connection string stays in
			// postgresql:// URI form and Provider is left at its default, so every CI run
			// also proves URI conversion + provider inference end-to-end.
			connectionString = CreatePostgresDatabase(adminUri);
		}
		else
		{
			string dbPath = Path.Combine(Path.GetTempPath(), $"activesync-it-{Guid.NewGuid():N}.db");
			_tempDbs.Add(dbPath);
			connectionString = $"Data Source={dbPath}";
		}

		if (withoutDav)
		{
			_localStoresConnectionString = connectionString;
			_localStoresIsPostgres = TestBackend.PostgresUri is not null;
		}

		Dictionary<string, string?> settings = new()
		{
			["ActiveSync:Database:ConnectionString"] = connectionString,
			["ActiveSync:Encryption:Key"] = TestEncryptionKey,
			["ActiveSync:ReadOnly"] = readOnly ? "true" : "false",
			// Short bounds so long-poll scenarios finish quickly.
			["ActiveSync:Eas:MinHeartbeatSeconds"] = "5",
			["ActiveSync:Eas:MaxHeartbeatSeconds"] = "120",
			["ActiveSync:Eas:DavPollSeconds"] = "5",
			["ActiveSync:Eas:UseImapIdle"] = fastWatchdogNoIdle ? "false" : "true",
			// TestServer requests all share one client key (no remote address), so the
			// suite's deliberate bad-credential tests would otherwise trip the shared
			// brute-force throttle for every later test.
			["ActiveSync:Auth:MaxFailures"] = "1000000",
			// ManageSieve for the Oof scenarios. Stalwart refuses AUTHENTICATE over plaintext
			// even in the lab config (ENCRYPT-NEEDED), so STARTTLS against its self-signed
			// certificate it is — which conveniently exercises the production TLS path.
			// Only contacted on Settings->Oof Set, so stacks without sieve are unaffected.
			["ActiveSync:Backends:Oof:Provider"] = "sieve",
			["ActiveSync:Backends:Oof:Host"] = TestBackend.SieveHost,
			["ActiveSync:Backends:Oof:Port"] = TestBackend.SievePort.ToString(),
			["ActiveSync:Backends:Oof:UseTls"] = TestBackend.SieveUseTls ? "true" : "false",
			["ActiveSync:Backends:Oof:AllowInvalidCertificates"] = "true"
		};
		if (jmap)
		{
			// Stalwart serves JMAP on the same HTTP listener as DAV; one session fills both
			// mail roles. AllowInvalidCertificates is harmless over the plaintext test URL.
			settings["ActiveSync:Backends:MailStore:Provider"] = "jmap";
			settings["ActiveSync:Backends:MailStore:BaseUrl"] = TestBackend.JmapUrl;
			settings["ActiveSync:Backends:MailStore:AllowInvalidCertificates"] = "true";
			settings["ActiveSync:Backends:MailSubmit:Provider"] = "jmap";
			settings["ActiveSync:Backends:MailSubmit:BaseUrl"] = TestBackend.JmapUrl;
			settings["ActiveSync:Backends:MailSubmit:AllowInvalidCertificates"] = "true";
			// OOF over JMAP VacationResponse (replaces the sieve provider for this factory).
			settings["ActiveSync:Backends:Oof:Provider"] = "jmap";
			settings["ActiveSync:Backends:Oof:BaseUrl"] = TestBackend.JmapUrl;
			settings["ActiveSync:Backends:Oof:AllowInvalidCertificates"] = "true";
		}
		else
		{
			settings["ActiveSync:Backends:MailStore:Provider"] = "imap";
			settings["ActiveSync:Backends:MailStore:Host"] = TestBackend.ImapHost;
			settings["ActiveSync:Backends:MailStore:Port"] = TestBackend.ImapPort.ToString();
			settings["ActiveSync:Backends:MailStore:UseSsl"] = "false";
			settings["ActiveSync:Backends:MailStore:Security"] = "None";
			if (TestBackend.MailSubmitProvider.Equals("jmap", StringComparison.OrdinalIgnoreCase)
			    && TestBackend.JmapUrl is { } submitUrl)
			{
				// Cyrus has no SMTP submission MSA (LMTP-only) but advertises JMAP submission;
				// exercise mail-flow over JMAP EmailSubmission while reading over IMAP.
				settings["ActiveSync:Backends:MailSubmit:Provider"] = "jmap";
				settings["ActiveSync:Backends:MailSubmit:BaseUrl"] = submitUrl;
				settings["ActiveSync:Backends:MailSubmit:AllowInvalidCertificates"] = "true";
			}
			else
			{
				settings["ActiveSync:Backends:MailSubmit:Provider"] = "smtp";
				settings["ActiveSync:Backends:MailSubmit:Host"] = TestBackend.SmtpHost;
				settings["ActiveSync:Backends:MailSubmit:Port"] = TestBackend.SmtpPort.ToString();
				settings["ActiveSync:Backends:MailSubmit:UseSsl"] = "false";
				settings["ActiveSync:Backends:MailSubmit:Security"] = "None";
			}
		}

		if (fastWatchdogNoIdle)
			settings["ActiveSync:Eas:WatchdogSeconds"] = "15";
		if (!withoutDav && TestBackend.DavUrl is { } davUrl)
		{
			settings["ActiveSync:Backends:Calendar:Provider"] = "caldav";
			settings["ActiveSync:Backends:Calendar:BaseUrl"] = davUrl;
			settings["ActiveSync:Backends:Calendar:HomeSetPath"] = TestBackend.DavHomeSetPath;
			settings["ActiveSync:Backends:Tasks:Provider"] = "caldav";
			settings["ActiveSync:Backends:Contacts:Provider"] = "carddav";
			settings["ActiveSync:Backends:Contacts:BaseUrl"] = davUrl;
			settings["ActiveSync:Backends:Contacts:HomeSetPath"] = TestBackend.DavContactsHomeSetPath;
		}

		if (overrides is not null)
			foreach ((string key, string? value) in overrides)
				settings[key] = value;

		return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			HostingAbstractionsWebHostBuilderExtensions
				.UseEnvironment(builder, Environments.Production);
			// Provider selection happens EAGERLY in Program.cs (AddSyncDatabase registers the
			// provider-specific context) and eager reads never see ConfigureAppConfiguration
			// overrides — host settings are the one channel that reaches them. Without this
			// the app registers the SQLite context and later feeds it the Postgres string.
			if (TestBackend.PostgresUri is not null)
				builder.UseSetting("ActiveSync:Database:ConnectionString",
					settings["ActiveSync:Database:ConnectionString"]);
			// Caller overrides go through the same host-settings channel: several of them
			// gate eager reads too (Metrics:Enabled decides service registrations).
			if (overrides is not null)
				foreach ((string key, string? value) in overrides)
					builder.UseSetting(key, value);
			builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
		});
	}

	/// <summary>
	///   Creates a fresh database on the AS_TEST_PG server; returns its URI. Never dropped:
	///   AS_TEST_PG is CI-only, and the CI Postgres container is discarded with the stack.
	/// </summary>
	private static string CreatePostgresDatabase(string adminUri)
	{
		if (!PostgresConnectionUri.TryConvert(adminUri, out string adminKeywordForm, out string? error))
			throw new InvalidOperationException($"AS_TEST_PG is not a usable postgresql:// URI: {error}");

		string databaseName = $"activesync_it_{Guid.NewGuid():N}";
		using (NpgsqlConnection admin = new(adminKeywordForm))
		{
			admin.Open();
			using NpgsqlCommand command = admin.CreateCommand();
			command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
			command.ExecuteNonQuery();
		}

		NpgsqlConnectionStringBuilder builder = new(adminKeywordForm);
		return $"postgresql://{Uri.EscapeDataString(builder.Username ?? "")}:" +
		       $"{Uri.EscapeDataString(builder.Password ?? "")}@{builder.Host}:{builder.Port}/{databaseName}";
	}

	private static async Task WarmDeliveryAsync(string from, string to)
	{
		string subject = $"warmup-{Guid.NewGuid():N}";
		using (SmtpClient smtp = new())
		{
			await smtp.ConnectAsync(TestBackend.SmtpHost, TestBackend.SmtpPort, SecureSocketOptions.None);
			await smtp.AuthenticateAsync(from, TestBackend.Password);
			MimeMessage message = new();
			message.From.Add(MailboxAddress.Parse(from));
			message.To.Add(MailboxAddress.Parse(to));
			message.Subject = subject;
			message.Body = new TextPart("plain") { Text = "delivery warm-up" };
			await smtp.SendAsync(message);
			await smtp.DisconnectAsync(true);
		}

		using ImapClient imap = new();
		await imap.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None);
		await imap.AuthenticateAsync(to, TestBackend.Password);
		DateTime deadline = DateTime.UtcNow.AddSeconds(90);
		while (DateTime.UtcNow < deadline)
		{
			await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
			IList<UniqueId> uids = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
			if (uids.Count > 0)
			{
				await imap.Inbox.AddFlagsAsync(uids, MessageFlags.Deleted, true);
				await imap.Inbox.ExpungeAsync();
				await imap.DisconnectAsync(true);
				return;
			}

			await imap.Inbox.CloseAsync();
			await Task.Delay(TimeSpan.FromSeconds(2));
		}

		throw new TimeoutException($"Warm-up mail {from} -> {to} was not delivered within 90 s.");
	}
}

[CollectionDefinition("gateway")]
public class GatewayCollection : ICollectionFixture<GatewayFixture>;
