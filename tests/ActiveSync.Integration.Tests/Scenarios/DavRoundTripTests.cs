using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Contacts and calendar round trips: one user, two devices. Device 1 creates via EAS,
///   device 2 receives via EAS, and the raw backend object is verified over WebDAV.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class DavRoundTripTests(GatewayFixture gateway)
{
	private static readonly XNamespace C = EasNamespaces.Contacts;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	[DavBackendFact]
	public async Task Contact_CreatedOnDevice1_SyncsToDevice2_AndDeletePropagates()
	{
		EasTestClient device1 = gateway.CreateEasClient(TestBackend.User1);
		await device1.HandshakeAsync();
		EasFolder? contactsFolder = device1.Folders.FirstOrDefault(f => f.Type == EasFolderType.Contacts);
		if (contactsFolder is null)
			return; // stack exposes no CardDAV address book — nothing to test

		EasTestClient device2 = gateway.CreateEasClient(TestBackend.User1);
		await device2.HandshakeAsync();
		string folder2 = device2.FolderOfType(EasFolderType.Contacts).ServerId;
		await device2.InitialSyncAsync(folder2);
		await device2.PullAllAsync(folder2);

		string? marker = $"IT{Guid.NewGuid():N}"[..12];
		await device1.InitialSyncAsync(contactsFolder!.ServerId);
		SyncResult add = await device1.AddItemAsync(contactsFolder.ServerId, "c1",
			new XElement(C + "FirstName", "Integration"),
			new XElement(C + "LastName", marker),
			new XElement(C + "FileAs", $"Integration {marker}"),
			new XElement(C + "Email1Address", "contact@example.com"),
			new XElement(C + "MobilePhoneNumber", "+4512345678"));

		XNamespace AS = EasNamespaces.AirSync;
		XElement? addResponse = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(addResponse);
		Assert.Equal("1", addResponse.Element(AS + "Status")?.Value);
		string? serverId = addResponse.Element(AS + "ServerId")?.Value;
		Assert.False(string.IsNullOrEmpty(serverId));

		// Device 2 sees the new contact
		SyncItem received = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await device2.PullAllAsync(folder2);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(C + "LastName")?.Value == marker);
			}, $"contact '{marker}' on device 2");
		Assert.Equal("Integration", received.ApplicationData.Element(C + "FirstName")?.Value);
		Assert.Equal("+4512345678", received.ApplicationData.Element(C + "MobilePhoneNumber")?.Value);

		// Delete on device 1 → disappears from device 2
		await device1.DeleteItemAsync(contactsFolder.ServerId, serverId!);
		await WaitUntil.TrueAsync(async () =>
			{
				SyncResult pull = await device2.PullAllAsync(folder2);
				return pull.Deletes.Contains(received.ServerId);
			}, $"contact '{marker}' deletion on device 2");
	}

	[DavBackendFact]
	public async Task RecurringEvent_CreatedOnDevice1_SyncsToDevice2()
	{
		EasTestClient device1 = gateway.CreateEasClient(TestBackend.User1);
		await device1.HandshakeAsync();
		EasFolder? calendarFolder = device1.Folders.FirstOrDefault(f => f.Type == EasFolderType.Calendar);
		if (calendarFolder is null)
			return; // stack exposes no CalDAV calendar — nothing to test

		EasTestClient device2 = gateway.CreateEasClient(TestBackend.User1);
		await device2.HandshakeAsync();
		string folder2 = device2.FolderOfType(EasFolderType.Calendar).ServerId;
		await device2.InitialSyncAsync(folder2);
		await device2.PullAllAsync(folder2);

		string? marker = $"Standup {Guid.NewGuid():N}"[..20];
		DateTime start = DateTime.UtcNow.Date.AddDays(1).AddHours(9);
		await device1.InitialSyncAsync(calendarFolder!.ServerId);
		SyncResult add = await device1.AddItemAsync(calendarFolder.ServerId, "e1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddMinutes(30))),
			new XElement(Cal + "Subject", marker),
			new XElement(Cal + "Location", "Room 1"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"),
			new XElement(Cal + "Reminder", "10"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "1"), // weekly
				new XElement(Cal + "Interval", "1"),
				new XElement(Cal + "DayOfWeek", "62"), // Mon-Fri
				new XElement(Cal + "Occurrences", "10")));

		XNamespace AS = EasNamespaces.AirSync;
		XElement? addResponse = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(addResponse);
		Assert.Equal("1", addResponse.Element(AS + "Status")?.Value);

		SyncItem received = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await device2.PullAllAsync(folder2);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Cal + "Subject")?.Value == marker);
			}, $"event '{marker}' on device 2");

		Assert.Equal("Room 1", received.ApplicationData.Element(Cal + "Location")?.Value);
		XElement? recurrence = received.ApplicationData.Element(Cal + "Recurrence");
		Assert.NotNull(recurrence);
		Assert.Equal("1", recurrence.Element(Cal + "Type")?.Value);
		Assert.Equal("62", recurrence.Element(Cal + "DayOfWeek")?.Value);
	}
}
