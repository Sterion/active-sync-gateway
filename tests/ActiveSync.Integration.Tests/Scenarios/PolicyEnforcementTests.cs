using System.Net;
using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Device security policy enforcement (ActiveSync:Policy): 449 until the current policy is
///   acknowledged, policy delivery in the Provision document, hash-based re-provision after a
///   config change, and the recovery-password escrow. These factories pin their state DB to a
///   known SQLite file so two gateway generations can share one device row and the escrow test
///   can assert the at-rest bytes.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class PolicyEnforcementTests(GatewayFixture gateway)
{
	private static readonly XNamespace PV = EasNamespaces.Provision;
	private static readonly XNamespace FH = EasNamespaces.FolderHierarchy;
	private static readonly XNamespace ST = EasNamespaces.Settings;

	[BackendFact]
	public async Task PolicyOn_UnprovisionedDeviceGets449_ProvisionDeliversDoc_ThenUnlocks()
	{
		string dbPath = TempDbPath();
		try
		{
			await using WebApplicationFactory<Program> factory =
				gateway.CreateIsolatedFactory(PolicyOverrides(dbPath, minPasswordLength: "4"));
			EasTestClient client = Client(factory);

			using HttpResponseMessage locked = await client.PostRawAsync("FolderSync", FolderSyncBody());
			Assert.Equal((HttpStatusCode)449, locked.StatusCode);

			// Phase 1 must carry the configured document, not the historical empty stub.
			XDocument? phase1 = await client.PostAsync("Provision", new XDocument(
				new XElement(PV + "Provision",
					new XElement(PV + "Policies",
						new XElement(PV + "Policy",
							new XElement(PV + "PolicyType", "MS-EAS-Provisioning-WBXML"))))));
			XElement? doc = phase1?.Root?
				.Element(PV + "Policies")?.Element(PV + "Policy")?
				.Element(PV + "Data")?.Element(PV + "EASProvisionDoc");
			Assert.NotNull(doc);
			Assert.Equal("1", doc.Element(PV + "DevicePasswordEnabled")?.Value);
			Assert.Equal("4", doc.Element(PV + "MinDevicePasswordLength")?.Value);
			Assert.Equal("1", doc.Element(PV + "PasswordRecoveryEnabled")?.Value);

			await client.ProvisionAsync();
			await client.FolderSyncAsync(); // asserts Status 1 internally

			// The header transport (plain query + X-MS-PolicyKey) must satisfy the gate too.
			using HttpResponseMessage viaHeader =
				await client.PostRawAsync("FolderSync", FolderSyncBody(), usePlainQuery: true);
			Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
		}
		finally
		{
			TryDelete(dbPath);
		}
	}

	[BackendFact]
	public async Task PolicyOn_MissingKeyOnKnownDevice_Gets449()
	{
		string dbPath = TempDbPath();
		try
		{
			await using WebApplicationFactory<Program> factory =
				gateway.CreateIsolatedFactory(PolicyOverrides(dbPath));
			EasTestClient provisioned = Client(factory);
			await provisioned.ProvisionAsync();
			await provisioned.FolderSyncAsync();

			// Same device id, no policy key — e.g. a wiped/re-set-up client reusing its id.
			EasTestClient amnesiac = Client(factory, provisioned.DeviceId);
			using HttpResponseMessage response = await amnesiac.PostRawAsync("FolderSync", FolderSyncBody());
			Assert.Equal((HttpStatusCode)449, response.StatusCode);
		}
		finally
		{
			TryDelete(dbPath);
		}
	}

	[BackendFact]
	public async Task PolicyChange_InvalidatesAcknowledgment_UntilReprovision()
	{
		string dbPath = TempDbPath();
		try
		{
			string deviceId;
			uint acknowledgedKey;
			await using (WebApplicationFactory<Program> generationOne =
				gateway.CreateIsolatedFactory(PolicyOverrides(dbPath, minPasswordLength: "4")))
			{
				EasTestClient client = Client(generationOne);
				await client.ProvisionAsync();
				await client.FolderSyncAsync();
				deviceId = client.DeviceId;
				acknowledgedKey = client.PolicyKey;
			}

			// Same state DB, stricter policy: the stored key is still valid but the hash the
			// device acknowledged is not — the gate must herd it back through Provision.
			await using WebApplicationFactory<Program> generationTwo =
				gateway.CreateIsolatedFactory(PolicyOverrides(dbPath, minPasswordLength: "8"));
			EasTestClient stale = Client(generationTwo, deviceId);
			stale.PolicyKey = acknowledgedKey;
			using HttpResponseMessage refused = await stale.PostRawAsync("FolderSync", FolderSyncBody());
			Assert.Equal((HttpStatusCode)449, refused.StatusCode);

			await stale.ProvisionAsync();
			await stale.FolderSyncAsync();
		}
		finally
		{
			TryDelete(dbPath);
		}
	}

	[BackendFact]
	public async Task RecoveryPassword_IsEscrowedSealed_WhenPolicyOffersIt()
	{
		string dbPath = TempDbPath();
		try
		{
			await using WebApplicationFactory<Program> factory =
				gateway.CreateIsolatedFactory(PolicyOverrides(dbPath));
			EasTestClient client = Client(factory);
			await client.ProvisionAsync();

			const string recoveryPassword = "recover-me-1234";
			XDocument? response = await client.PostAsync("Settings", DevicePasswordSet(recoveryPassword));
			Assert.Equal("1",
				response?.Root?.Element(ST + "DevicePassword")?.Element(ST + "Status")?.Value);

			// At rest: sealed (v1: prefix), never the plaintext.
			string stored = ReadRecoveryPassword(dbPath, client.DeviceId);
			Assert.StartsWith("v1:", stored, StringComparison.Ordinal);
			Assert.DoesNotContain(recoveryPassword, stored, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			TryDelete(dbPath);
		}
	}

	[BackendFact]
	public async Task RecoveryPassword_IsRefused_WhenPolicyDisabled()
	{
		// The shared default gateway runs without a policy — escrow must answer Status 5,
		// not silently pretend to store something the operator can never read back.
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		XDocument? response = await client.PostAsync("Settings", DevicePasswordSet("whatever-123"));
		Assert.Equal("5",
			response?.Root?.Element(ST + "DevicePassword")?.Element(ST + "Status")?.Value);
	}

	// ---------- helpers ----------

	private static Dictionary<string, string?> PolicyOverrides(string dbPath, string minPasswordLength = "4")
	{
		return new Dictionary<string, string?>
		{
			// Pinned SQLite state DB (regardless of the suite's provider) so generations can
			// share device rows and tests can read the stored bytes directly.
			["ActiveSync:Database:ConnectionString"] = $"Data Source={dbPath}",
			["ActiveSync:Policy:Enabled"] = "true",
			["ActiveSync:Policy:DevicePasswordEnabled"] = "true",
			["ActiveSync:Policy:MinDevicePasswordLength"] = minPasswordLength,
			["ActiveSync:Policy:PasswordRecoveryEnabled"] = "true"
		};
	}

	private static EasTestClient Client(WebApplicationFactory<Program> factory, string? deviceId = null)
	{
		return new EasTestClient(
			factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			TestBackend.User1, TestBackend.Password,
			deviceId ?? $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
	}

	private static XDocument FolderSyncBody()
	{
		return new XDocument(new XElement(FH + "FolderSync", new XElement(FH + "SyncKey", "0")));
	}

	private static XDocument DevicePasswordSet(string password)
	{
		return new XDocument(new XElement(ST + "Settings",
			new XElement(ST + "DevicePassword",
				new XElement(ST + "Set",
					new XElement(ST + "Password", password)))));
	}

	private static string ReadRecoveryPassword(string dbPath, string deviceId)
	{
		using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
		connection.Open();
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT RecoveryPasswordProtected FROM Devices WHERE DeviceId = $device";
		command.Parameters.AddWithValue("$device", deviceId);
		object? value = command.ExecuteScalar();
		Assert.NotNull(value);
		return (string)value;
	}

	private static string TempDbPath()
	{
		return Path.Combine(Path.GetTempPath(), $"activesync-policy-{Guid.NewGuid():N}.db");
	}

	private static void TryDelete(string dbPath)
	{
		try
		{
			File.Delete(dbPath);
		}
		catch (IOException)
		{
			// still locked on Windows — temp files get cleaned eventually
		}
	}
}
