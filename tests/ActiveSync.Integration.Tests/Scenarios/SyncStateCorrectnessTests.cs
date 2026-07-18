using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Regressions for two sync-state correctness bugs: GetItemEstimate must not mutate the
///   collection sync state, and a SyncKey-0 FolderSync must return the full hierarchy even
///   when the device previously acknowledged it (re-provisioning).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class SyncStateCorrectnessTests(GatewayFixture gateway)
{
	private static readonly XNamespace GIE = EasNamespaces.GetItemEstimate;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	[BackendFact]
	public async Task GetItemEstimate_WithStaleSyncKey0_DoesNotResetCollectionState()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		string key = await client.InitialSyncAsync(inbox); // key is now > 0

		// An anomalous GetItemEstimate carrying SyncKey 0 used to reset the snapshot/key.
		XDocument? estimate = await client.PostAsync("GetItemEstimate", new XDocument(
			new XElement(GIE + "GetItemEstimate",
				new XElement(GIE + "Collections",
					new XElement(GIE + "Collection",
						new XElement(AS + "SyncKey", "0"),
						new XElement(AS + "CollectionId", inbox))))));
		Assert.Equal("1", estimate?.Root?.Element(GIE + "Response")?.Element(GIE + "Status")?.Value);

		// The real sync key must still validate — proof the estimate did not touch state.
		SyncResult afterEstimate = await client.SyncAsync(inbox);
		Assert.Equal("1", afterEstimate.Status ?? "1");
		Assert.NotEqual("0", key);
	}

	[BackendFact]
	public async Task FolderSync_RestartFromZero_ReturnsFullHierarchy()
	{
		string deviceId = $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant();

		// First device session acknowledges the whole hierarchy (DeviceFolder rows persist).
		EasTestClient first = gateway.CreateEasClient(TestBackend.User1, deviceId);
		await first.HandshakeAsync();
		Assert.Contains(first.Folders, f => f.Type == EasFolderType.Inbox);

		// Same device re-provisions: a fresh client starts FolderSync from key 0 again and
		// must receive every folder, not Count=0.
		EasTestClient reprovisioned = gateway.CreateEasClient(TestBackend.User1, deviceId);
		List<EasFolder> folders = await reprovisioned.FolderSyncAsync();
		Assert.NotEmpty(folders);
		Assert.Contains(folders, f => f.Type == EasFolderType.Inbox);
	}
}
