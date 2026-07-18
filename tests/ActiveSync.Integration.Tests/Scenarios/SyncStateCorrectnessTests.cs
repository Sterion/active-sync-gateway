using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Regressions for sync-state correctness bugs: GetItemEstimate must not mutate the
///   collection sync state, a SyncKey-0 FolderSync must return the full hierarchy even
///   when the device previously acknowledged it (re-provisioning), and a retried Sync Add
///   (lost response) must not create a duplicate item on the backend.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class SyncStateCorrectnessTests(GatewayFixture gateway)
{
	private static readonly XNamespace GIE = EasNamespaces.GetItemEstimate;
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace C = EasNamespaces.Contacts;

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

	[BackendFact]
	public async Task Sync_RetriedAddAfterLostResponse_DoesNotDuplicateItem()
	{
		EasTestClient client = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await client.HandshakeAsync();
		string contacts = client.FolderOfType(EasFolderType.Contacts).ServerId;
		await client.InitialSyncAsync(contacts);
		await client.PullAllAsync(contacts);

		string marker = $"Retry{Guid.NewGuid():N}"[..12];
		string keyBeforeAdd = client.SyncKeys[contacts];
		SyncResult first = await client.AddItemAsync(contacts, "r1",
			new XElement(C + "FirstName", "Once"),
			new XElement(C + "LastName", marker));
		string firstServerId = AssertAdded(first);

		// The response "never arrived": the client re-posts the identical Add (same ClientId,
		// same data) under the previous sync key. The server must replay, not create again.
		client.SyncKeys[contacts] = keyBeforeAdd;
		SyncResult retry = await client.AddItemAsync(contacts, "r1",
			new XElement(C + "FirstName", "Once"),
			new XElement(C + "LastName", marker));
		string retryServerId = AssertAdded(retry);
		Assert.Equal(firstServerId, retryServerId);
		Assert.Equal(first.SyncKey, retry.SyncKey);

		// A fresh device (same user, shared local store) sees exactly one copy on the backend.
		EasTestClient verifier = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await verifier.HandshakeAsync();
		string verifierContacts = verifier.FolderOfType(EasFolderType.Contacts).ServerId;
		await verifier.InitialSyncAsync(verifierContacts);
		SyncResult all = await verifier.PullAllAsync(verifierContacts);
		Assert.Single(all.Adds, a => a.ApplicationData.Element(C + "LastName")?.Value == marker);
	}

	private static string AssertAdded(SyncResult result)
	{
		XElement? add = result.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(add);
		Assert.Equal("1", add.Element(AS + "Status")?.Value);
		string serverId = add.Element(AS + "ServerId")?.Value ?? "";
		Assert.False(string.IsNullOrEmpty(serverId));
		return serverId;
	}
}
