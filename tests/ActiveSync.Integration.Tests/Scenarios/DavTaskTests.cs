using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   CalDAV-backed tasks (VTODO collection named by CalDav.TaskFolder). The test creates a
///   "Tasks" collection via MKCALENDAR when the server allows it and skips gracefully when
///   it does not (the mechanism is exercised for real against Axigen).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class DavTaskTests(GatewayFixture gateway)
{
	private static readonly XNamespace T = EasNamespaces.Tasks;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	[BackendFact]
	public async Task Task_CreatedOnDevice1_SyncsToDevice2_AndCompletionPropagates()
	{
		if (!await EnsureTasksCollectionAsync(TestBackend.User1))
			return; // server without MKCALENDAR/tasks support — nothing to test

		EasTestClient device1 = gateway.CreateEasClient(TestBackend.User1);
		await device1.HandshakeAsync();
		EasFolder? tasksFolder = device1.Folders.FirstOrDefault(f => f.Type == EasFolderType.Tasks);
		if (tasksFolder is null)
			return; // gateway found no VTODO collection named "Tasks" on this stack

		EasTestClient device2 = gateway.CreateEasClient(TestBackend.User1);
		await device2.HandshakeAsync();
		string tasks2 = device2.FolderOfType(EasFolderType.Tasks).ServerId;
		await device2.InitialSyncAsync(tasks2);
		await device2.PullAllAsync(tasks2);

		string? marker = $"Task {Guid.NewGuid():N}"[..18];
		await device1.InitialSyncAsync(tasksFolder.ServerId);
		await device1.PullAllAsync(tasksFolder.ServerId);
		SyncResult add = await device1.AddItemAsync(tasksFolder.ServerId, "t1",
			new XElement(T + "Subject", marker),
			new XElement(T + "Complete", "0"),
			new XElement(T + "UtcDueDate", EasDateTime.ToLong(DateTime.UtcNow.Date.AddDays(3))));
		XElement? addResponse = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(addResponse);
		Assert.Equal("1", addResponse.Element(AS + "Status")?.Value);
		string? serverId = addResponse.Element(AS + "ServerId")?.Value;
		Assert.False(string.IsNullOrEmpty(serverId));

		SyncItem received = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(tasks2)).Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(T + "Subject")?.Value == marker),
			$"task '{marker}' on device 2");
		Assert.Equal("0", received.ApplicationData.Element(T + "Complete")?.Value);

		// Complete on device 1 → device 2 sees the change
		await device1.ChangeItemAsync(tasksFolder.ServerId, serverId!,
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
	public async Task RecurringTask_RoundTrips_WithRecurrenceElement()
	{
		if (!await EnsureTasksCollectionAsync(TestBackend.User2))
			return; // server without MKCALENDAR/tasks support — nothing to test

		EasTestClient writer = gateway.CreateEasClient(TestBackend.User2);
		await writer.HandshakeAsync();
		EasFolder? tasksFolder = writer.Folders.FirstOrDefault(f => f.Type == EasFolderType.Tasks);
		if (tasksFolder is null)
			return;

		string marker = $"Recur {Guid.NewGuid():N}"[..18];
		await writer.InitialSyncAsync(tasksFolder.ServerId);
		await writer.PullAllAsync(tasksFolder.ServerId);
		string startDate = EasDateTime.ToLong(DateTime.UtcNow.Date.AddDays(1));
		SyncResult add = await writer.AddItemAsync(tasksFolder.ServerId, "tr1",
			new XElement(T + "Subject", marker),
			new XElement(T + "Complete", "0"),
			new XElement(T + "StartDate", startDate),
			new XElement(T + "Recurrence",
				new XElement(T + "Type", "1"),
				new XElement(T + "Start", startDate),
				new XElement(T + "DayOfWeek", "62"), // weekdays
				new XElement(T + "Interval", "1")));
		XElement? addResponse = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(addResponse);
		Assert.Equal("1", addResponse.Element(AS + "Status")?.Value);

		// A second device re-reads the stored VTODO — the RRULE must come back as the
		// same Recurrence shape.
		EasTestClient observer = gateway.CreateEasClient(TestBackend.User2);
		await observer.HandshakeAsync();
		string tasks2 = observer.FolderOfType(EasFolderType.Tasks).ServerId;
		await observer.InitialSyncAsync(tasks2);
		SyncItem received = await WaitUntil.ResultAsync(async () =>
				(await observer.PullAllAsync(tasks2)).Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(T + "Subject")?.Value == marker),
			$"recurring task '{marker}' on the second device");
		XElement? recurrence = received.ApplicationData.Element(T + "Recurrence");
		Assert.NotNull(recurrence);
		Assert.Equal("1", recurrence.Element(T + "Type")?.Value);
		Assert.Equal("62", recurrence.Element(T + "DayOfWeek")?.Value);
		Assert.NotNull(recurrence.Element(T + "Start"));
	}

	/// <summary>Ensures a "Tasks" calendar collection exists in the user's home set.</summary>
	private static async Task<bool> EnsureTasksCollectionAsync(string user)
	{
		if (TestBackend.DavUrl is not { } davUrl)
			return false;
		BackendCredentials credentials = new(user, TestBackend.Password);
		using WebDavClient dav = new(new Uri(davUrl), credentials);
		string home;
		try
		{
			home = string.IsNullOrEmpty(TestBackend.DavHomeSetPath)
				? await DavDiscovery.DiscoverHomeSetAsync(
					dav, "/.well-known/caldav", DavNs.CalDav + "calendar-home-set", CancellationToken.None)
				: DavDiscovery.ExpandTemplate(TestBackend.DavHomeSetPath, user);
		}
		catch (BackendException)
		{
			return false;
		}

		string target = home.TrimEnd('/') + "/Tasks/";
		using HttpClient http = new() { BaseAddress = new Uri(davUrl) };
		http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
			Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{TestBackend.Password}")));
		using HttpRequestMessage mkcalendar = new(new HttpMethod("MKCALENDAR"), target);
		try
		{
			using HttpResponseMessage response = await http.SendAsync(mkcalendar);
			// 2xx = created; 405 usually = already exists. Verify by PROPFIND either way.
		}
		catch (HttpRequestException)
		{
			return false;
		}

		try
		{
			await dav.PropfindAsync(target, 0, new XElement(DavNs.D + "propfind",
				new XElement(DavNs.D + "prop", new XElement(DavNs.D + "resourcetype"))), CancellationToken.None);
			return true;
		}
		catch (BackendException)
		{
			return false;
		}
	}
}
