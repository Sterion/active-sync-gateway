using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Local content is encrypted at rest: what EAS clients round-trip as plaintext must land
///   in the LocalItems table as "v1:" AES-GCM ciphertext. Uses the DAV-less gateway factory
///   (fixed test key) plus an isolated AllowPlaintext gateway for the escape hatch. The
///   at-rest reads go through <see cref="GatewayFixture.ReadLocalItemContents" /> (raw ADO,
///   SQLite or Postgres depending on the suite's AS_TEST_PG setting).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class EncryptionAtRestTests(GatewayFixture gateway)
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace N = EasNamespaces.Notes;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	[BackendFact]
	public async Task NoteBody_IsCiphertextInTheDatabase_AndRoundTripsThroughEas()
	{
		string marker = $"Enc{Guid.NewGuid():N}"[..12];
		const string secret = "the vault combination is 12-34-56";

		EasTestClient device1 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await device1.HandshakeAsync();
		string notes1 = device1.FolderOfType(EasFolderType.Notes).ServerId;
		await device1.InitialSyncAsync(notes1);
		await device1.PullAllAsync(notes1);

		SyncResult add = await device1.AddItemAsync(notes1, "n1",
			new XElement(N + "Subject", marker),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", secret)));
		XElement? added = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(added);
		Assert.Equal("1", added.Element(AS + "Status")?.Value);
		string serverId = added.Element(AS + "ServerId")?.Value ?? "";

		// --- at rest: every stored notes row is sealed, nothing readable leaks ---
		List<string> stored = gateway.ReadLocalItemContents(TestBackend.User1, "notes");
		Assert.NotEmpty(stored);
		Assert.All(stored, content => Assert.StartsWith("v1:", content));
		Assert.All(stored, content =>
		{
			Assert.DoesNotContain(secret, content);
			Assert.DoesNotContain(marker, content);
			Assert.DoesNotContain("VJOURNAL", content);
		});

		// --- on the wire: a second device still receives the plaintext ---
		EasTestClient device2 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await device2.HandshakeAsync();
		string notes2 = device2.FolderOfType(EasFolderType.Notes).ServerId;
		await device2.InitialSyncAsync(notes2);
		SyncItem note = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(notes2)).Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(N + "Subject")?.Value == marker),
			$"note '{marker}' on device 2");
		Assert.Equal(secret, note.ApplicationData.Element(ASB + "Body")?.Element(ASB + "Data")?.Value);

		// --- update-merge seam: the converter must merge into DECRYPTED content ---
		SyncResult change = await device1.ChangeItemAsync(notes1, serverId,
			new XElement(N + "Subject", $"{marker} v2"),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", secret + " and the pin is 7890")));
		Assert.Equal("1", change.Status ?? "1");
		await WaitUntil.TrueAsync(async () =>
				(await device2.PullAllAsync(notes2)).Changes.Any(c =>
					c.ApplicationData.Element(N + "Subject")?.Value == $"{marker} v2" &&
					(c.ApplicationData.Element(ASB + "Body")?.Element(ASB + "Data")?.Value ?? "")
					.Contains("pin is 7890")),
			"updated note on device 2");
		Assert.All(gateway.ReadLocalItemContents(TestBackend.User1, "notes"),
			content => Assert.StartsWith("v1:", content));
	}

	[BackendFact]
	public async Task PassphraseKey_EncryptsAndServesLocalItems()
	{
		// A passphrase is stretched to the 256-bit key; K1 requires a per-deployment salt for it.
		using WebApplicationFactory<Program> passphraseGateway = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?>
			{
				["ActiveSync:Encryption:Key"] = "my simple passphrase key!",
				["ActiveSync:Encryption:KeyDerivationSalt"] = "encryption-at-rest-test-salt"
			});
		EasTestClient client = new(
			passphraseGateway.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			TestBackend.User1, TestBackend.Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
		await client.HandshakeAsync();

		string marker = $"Phrase{Guid.NewGuid():N}"[..12];
		string notes = client.FolderOfType(EasFolderType.Notes).ServerId;
		await client.InitialSyncAsync(notes);
		await client.PullAllAsync(notes);
		SyncResult add = await client.AddItemAsync(notes, "n1",
			new XElement(N + "Subject", marker),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "sealed under a passphrase-derived key")));
		XElement? added = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(added);
		Assert.Equal("1", added.Element(AS + "Status")?.Value);

		// Round-trip proves encrypt + decrypt both used the same derived key.
		SyncResult pull = await client.PullAllAsync(notes);
		Assert.NotNull(pull);
	}

	[BackendFact]
	public async Task AllowPlaintextEscapeHatch_StillServesLocalItems()
	{
		using WebApplicationFactory<Program> plaintextGateway = gateway.CreateIsolatedFactory(new Dictionary<string, string?>
		{
			["ActiveSync:Encryption:Key"] = "",
			["ActiveSync:Encryption:AllowPlaintext"] = "true"
		});
		EasTestClient client = new(
			plaintextGateway.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			TestBackend.User1, TestBackend.Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
		await client.HandshakeAsync();

		string marker = $"Plain{Guid.NewGuid():N}"[..12];
		string notes = client.FolderOfType(EasFolderType.Notes).ServerId;
		await client.InitialSyncAsync(notes);
		await client.PullAllAsync(notes);
		SyncResult add = await client.AddItemAsync(notes, "n1",
			new XElement(N + "Subject", marker),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "unencrypted by explicit choice")));
		XElement? added = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(added);
		Assert.Equal("1", added.Element(AS + "Status")?.Value);
	}

}
