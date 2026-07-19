using System.Diagnostics;
using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Local (gateway-database) content stores: a mail-only configuration still serves
///   Contacts, Calendar and Notes — visible to all of the user's ActiveSync devices,
///   nowhere else. Uses the DAV-less gateway factory.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class LocalStoreTests(GatewayFixture gateway)
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace C = EasNamespaces.Contacts;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace N = EasNamespaces.Notes;
	private static readonly XNamespace T = EasNamespaces.Tasks;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	[BackendFact]
	public async Task FolderSync_ExposesLocalContactsCalendarAndNotes()
	{
		EasTestClient client = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await client.HandshakeAsync();

		Assert.Equal(EasFolderType.Contacts, client.FolderOfType(EasFolderType.Contacts).Type);
		Assert.Equal(EasFolderType.Calendar, client.FolderOfType(EasFolderType.Calendar).Type);
		Assert.Equal(EasFolderType.Notes, client.FolderOfType(EasFolderType.Notes).Type);
		Assert.Equal(EasFolderType.Tasks, client.FolderOfType(EasFolderType.Tasks).Type);
	}

	[BackendFact]
	public async Task Task_RoundTripAcrossTwoDevices_WithCompletion()
	{
		EasTestClient device1 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await device1.HandshakeAsync();
		EasTestClient device2 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await device2.HandshakeAsync();

		string tasks1 = device1.FolderOfType(EasFolderType.Tasks).ServerId;
		string tasks2 = device2.FolderOfType(EasFolderType.Tasks).ServerId;
		await device1.InitialSyncAsync(tasks1);
		await device1.PullAllAsync(tasks1);
		await device2.InitialSyncAsync(tasks2);
		await device2.PullAllAsync(tasks2);

		string? marker = $"LT{Guid.NewGuid():N}"[..12];
		SyncResult add = await device1.AddItemAsync(tasks1, "t1",
			new XElement(T + "Subject", marker),
			new XElement(T + "Complete", "0"),
			new XElement(T + "Importance", "2"),
			new XElement(T + "UtcDueDate", EasDateTime.ToLong(DateTime.UtcNow.Date.AddDays(5))));
		AssertAdded(add, out string taskServerId);

		SyncItem received = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(tasks2)).Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(T + "Subject")?.Value == marker),
			$"task '{marker}' on device 2");
		Assert.Equal("0", received.ApplicationData.Element(T + "Complete")?.Value);
		Assert.Equal("2", received.ApplicationData.Element(T + "Importance")?.Value);

		await device1.ChangeItemAsync(tasks1, taskServerId,
			new XElement(T + "Subject", marker),
			new XElement(T + "Complete", "1"),
			new XElement(T + "DateCompleted", EasDateTime.ToLong(DateTime.UtcNow)));
		await WaitUntil.TrueAsync(async () =>
				(await device2.PullAllAsync(tasks2)).Changes.Any(c =>
					c.ApplicationData.Element(T + "Subject")?.Value == marker &&
					c.ApplicationData.Element(T + "Complete")?.Value == "1"),
			"task completion on device 2");
	}

	[BackendFact]
	public async Task ContactEventAndNote_RoundTripAcrossTwoDevices()
	{
		EasTestClient device1 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await device1.HandshakeAsync();
		EasTestClient device2 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await device2.HandshakeAsync();

		string? marker = $"LS{Guid.NewGuid():N}"[..12];

		// --- create one item of each class on device 1 ---
		string contacts1 = device1.FolderOfType(EasFolderType.Contacts).ServerId;
		string calendar1 = device1.FolderOfType(EasFolderType.Calendar).ServerId;
		string notes1 = device1.FolderOfType(EasFolderType.Notes).ServerId;
		await device1.InitialSyncAsync(contacts1);
		await device1.InitialSyncAsync(calendar1);
		await device1.InitialSyncAsync(notes1);
		await device1.PullAllAsync(contacts1);
		await device1.PullAllAsync(calendar1);
		await device1.PullAllAsync(notes1);

		SyncResult contactAdd = await device1.AddItemAsync(contacts1, "c1",
			new XElement(C + "FirstName", "Local"),
			new XElement(C + "LastName", marker),
			new XElement(C + "Email1Address", "local@example.com"));
		AssertAdded(contactAdd, out string contactServerId);

		DateTime start = DateTime.UtcNow.Date.AddDays(2).AddHours(10);
		SyncResult eventAdd = await device1.AddItemAsync(calendar1, "e1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddHours(1))),
			new XElement(Cal + "Subject", $"Meet {marker}"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"));
		AssertAdded(eventAdd, out _);

		SyncResult noteAdd = await device1.AddItemAsync(notes1, "n1",
			new XElement(N + "Subject", $"Note {marker}"),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "remember the milk")));
		AssertAdded(noteAdd, out string noteServerId);

		// --- device 2 receives all three ---
		string contacts2 = device2.FolderOfType(EasFolderType.Contacts).ServerId;
		string calendar2 = device2.FolderOfType(EasFolderType.Calendar).ServerId;
		string notes2 = device2.FolderOfType(EasFolderType.Notes).ServerId;
		await device2.InitialSyncAsync(contacts2);
		await device2.InitialSyncAsync(calendar2);
		await device2.InitialSyncAsync(notes2);

		SyncItem contact = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(contacts2)).Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(C + "LastName")?.Value == marker),
			$"contact '{marker}' on device 2");
		Assert.Equal("Local", contact.ApplicationData.Element(C + "FirstName")?.Value);

		SyncItem evt = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(calendar2)).Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Cal + "Subject")?.Value == $"Meet {marker}"),
			$"event '{marker}' on device 2");
		Assert.Equal(EasDateTime.ToCompact(start), evt.ApplicationData.Element(Cal + "StartTime")?.Value);

		SyncItem note = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(notes2)).Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(N + "Subject")?.Value == $"Note {marker}"),
			$"note '{marker}' on device 2");
		Assert.Equal("remember the milk",
			note.ApplicationData.Element(ASB + "Body")?.Element(ASB + "Data")?.Value);

		// --- change the note on device 1, device 2 sees the change ---
		SyncResult change = await device1.ChangeItemAsync(notes1, noteServerId,
			new XElement(N + "Subject", $"Note {marker} v2"),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "milk and eggs")));
		Assert.Equal("1", change.Status ?? "1");
		await WaitUntil.TrueAsync(async () =>
				(await device2.PullAllAsync(notes2)).Changes.Any(c =>
					c.ApplicationData.Element(N + "Subject")?.Value == $"Note {marker} v2"),
			"note change on device 2");

		// --- delete the contact on device 1, device 2 sees the delete ---
		await device1.DeleteItemAsync(contacts1, contactServerId);
		await WaitUntil.TrueAsync(async () =>
				(await device2.PullAllAsync(contacts2)).Deletes.Contains(contact.ServerId),
			"contact deletion on device 2");
	}

	[BackendFact]
	public async Task Ping_WakesInstantly_WhenOtherDeviceAddsContact()
	{
		EasTestClient device1 = gateway.CreateLocalStoresEasClient(TestBackend.User2);
		await device1.HandshakeAsync();
		EasTestClient device2 = gateway.CreateLocalStoresEasClient(TestBackend.User2);
		await device2.HandshakeAsync();

		string contacts1 = device1.FolderOfType(EasFolderType.Contacts).ServerId;
		string contacts2 = device2.FolderOfType(EasFolderType.Contacts).ServerId;
		await device1.InitialSyncAsync(contacts1);
		await device1.PullAllAsync(contacts1);
		await device2.InitialSyncAsync(contacts2);
		await device2.PullAllAsync(contacts2);

		Task<(string Status, List<string> ChangedFolders)> pingTask = device2.PingAsync(60, contacts2);
		await Task.Delay(TimeSpan.FromSeconds(2));
		await device1.AddItemAsync(contacts1, "c1",
			new XElement(C + "FirstName", "Ping"),
			new XElement(C + "LastName", $"Wake{Guid.NewGuid():N}"[..10]));

		(string status, List<string> changed) = await pingTask;

		// Device 2 sees device 1's add via the in-process local-change notifier (or the watchdog
		// backstop) — status 2 is the functional guarantee. No wall-clock assertion: push latency is
		// unstable under load.
		Assert.Equal("2", status);
		Assert.Contains(contacts2, changed);
	}

	[BackendFact]
	public async Task LocalData_IsIsolatedPerUser()
	{
		string? marker = $"Iso{Guid.NewGuid():N}"[..12];

		EasTestClient user1 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await user1.HandshakeAsync();
		string notes1 = user1.FolderOfType(EasFolderType.Notes).ServerId;
		await user1.InitialSyncAsync(notes1);
		await user1.PullAllAsync(notes1);
		SyncResult add = await user1.AddItemAsync(notes1, "n1",
			new XElement(N + "Subject", marker),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "secret")));
		AssertAdded(add, out _);

		EasTestClient user2 = gateway.CreateLocalStoresEasClient(TestBackend.User2);
		await user2.HandshakeAsync();
		string notes2 = user2.FolderOfType(EasFolderType.Notes).ServerId;
		await user2.InitialSyncAsync(notes2);
		SyncResult pull = await user2.PullAllAsync(notes2);
		Assert.DoesNotContain(pull.Adds, a =>
			a.ApplicationData.Element(N + "Subject")?.Value == marker);
	}

	[BackendEnforcesAuthFact]
	public async Task LocalStores_RequireSuccessfulImapAuthentication()
	{
		// Local data has no backend of its own — access is gated by the IMAP login probe
		// that fronts every EAS request.
		using HttpClient http = gateway.LocalStoresFactory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		string wrong = Convert.ToBase64String(
			Encoding.UTF8.GetBytes($"{TestBackend.User1}:definitely-wrong-password"));
		using HttpRequestMessage request = new(HttpMethod.Post,
			$"/Microsoft-Server-ActiveSync?Cmd=FolderSync&User={Uri.EscapeDataString(TestBackend.User1)}&DeviceId=DEVBADPW1&DeviceType=Test");
		request.Headers.TryAddWithoutValidation("Authorization", $"Basic {wrong}");
		request.Headers.TryAddWithoutValidation("MS-ASProtocolVersion", "14.1");
		request.Content = new ByteArrayContent([]);
		request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/vnd.ms-sync.wbxml");

		HttpResponseMessage response = await http.SendAsync(request);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	private static void AssertAdded(SyncResult result, out string serverId)
	{
		XElement? add = result.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(add);
		Assert.Equal("1", add.Element(AS + "Status")?.Value);
		serverId = add.Element(AS + "ServerId")?.Value ?? "";
		Assert.False(string.IsNullOrEmpty(serverId));
	}
}
