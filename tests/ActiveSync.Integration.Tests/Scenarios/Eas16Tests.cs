using System.Net;
using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   EAS 16.1 behavior: version advertisement, the structured airsyncbase:Location shape,
///   occurrence deletes via InstanceId, drafts sync (IsDraft, email2:Send submit), event
///   attachments (inline calatt:: references incl. ItemOperations fetch) and the Find
///   command. A parallel 14.1 client asserts the legacy shapes stayed untouched.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class Eas16Tests(GatewayFixture gateway)
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace E = EasNamespaces.Email;
	private static readonly XNamespace E2 = EasNamespaces.Email2;
	private static readonly XNamespace F = EasNamespaces.Find;
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;

	[BackendFact]
	public async Task Options_Advertises161_AndFind()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		using HttpResponseMessage response = await client.OptionsAsync();
		string versions = response.Headers.GetValues("MS-ASProtocolVersions").Single();
		Assert.Contains("16.1", versions);
		Assert.DoesNotContain("2.5", versions);
		Assert.Contains("Find", response.Headers.GetValues("MS-ASProtocolCommands").Single());
	}

	[BackendFact]
	public async Task Calendar_LocationShape_FollowsProtocolVersion_AndInstanceIdDeletesOneOccurrence()
	{
		EasTestClient v16 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		v16.ProtocolVersion = "16.1";
		await v16.HandshakeAsync();
		string marker = $"E16{Guid.NewGuid():N}"[..10];
		string calendar = v16.FolderOfType(EasFolderType.Calendar).ServerId;
		await v16.InitialSyncAsync(calendar);
		await v16.PullAllAsync(calendar);

		// A 16.1 client creates a recurring event carrying the structured location.
		DateTime start = DateTime.UtcNow.Date.AddDays(3).AddHours(9);
		SyncResult add = await v16.AddItemAsync(calendar, "e16",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "ClientUid", $"client-uid-{marker}"),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddHours(1))),
			new XElement(Cal + "Subject", $"Standup {marker}"),
			new XElement(ASB + "Location", new XElement(ASB + "DisplayName", "War room")),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "0"),
				new XElement(Cal + "Interval", "1"),
				new XElement(Cal + "Occurrences", "5")),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"));
		Assert.Equal("1", add.Status);
		string serverId = AddedServerId(add);

		// A writing device already carries its own item in the snapshot, so observations
		// use SECOND devices: one 16.1, one 14.1 — same event, different Location shapes.
		EasTestClient observer16 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		observer16.ProtocolVersion = "16.1";
		await observer16.HandshakeAsync();
		string calendar16 = observer16.FolderOfType(EasFolderType.Calendar).ServerId;
		await observer16.InitialSyncAsync(calendar16);
		SyncItem v16View = await WaitUntil.ResultAsync(async () =>
				(await observer16.PullAllAsync(calendar16)).Adds
				.FirstOrDefault(a => a.ApplicationData.Element(Cal + "Subject")?.Value == $"Standup {marker}"),
			"16.1 view of the event");
		Assert.Equal("War room",
			v16View.ApplicationData.Element(ASB + "Location")?.Element(ASB + "DisplayName")?.Value);
		Assert.Null(v16View.ApplicationData.Element(Cal + "Location"));

		EasTestClient v14 = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await v14.HandshakeAsync();
		string calendar14 = v14.FolderOfType(EasFolderType.Calendar).ServerId;
		await v14.InitialSyncAsync(calendar14);
		SyncItem v14View = await WaitUntil.ResultAsync(async () =>
				(await v14.PullAllAsync(calendar14)).Adds
				.FirstOrDefault(a => a.ApplicationData.Element(Cal + "Subject")?.Value == $"Standup {marker}"),
			"14.1 view of the event");
		Assert.Equal("War room", v14View.ApplicationData.Element(Cal + "Location")?.Value);
		Assert.Null(v14View.ApplicationData.Element(ASB + "Location"));

		// Occurrence delete: Delete + InstanceId cancels one instance, not the series.
		DateTime secondOccurrence = start.AddDays(1);
		SyncResult occurrenceDelete = await v16.SyncAsync(calendar, new XElement(AS + "Commands",
			new XElement(AS + "Delete",
				new XElement(AS + "ServerId", serverId),
				new XElement(ASB + "InstanceId",
					secondOccurrence.ToString("yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'",
						System.Globalization.CultureInfo.InvariantCulture)))));
		Assert.Equal("1", occurrenceDelete.Status);

		SyncItem afterDelete = await WaitUntil.ResultAsync(async () =>
				(await observer16.PullAllAsync(calendar16)).Changes
				.FirstOrDefault(c => c.ApplicationData.Element(Cal + "Subject")?.Value == $"Standup {marker}"),
			"event with the cancelled occurrence");
		XElement? exception = afterDelete.ApplicationData
			.Element(Cal + "Exceptions")?.Elements(Cal + "Exception")
			.FirstOrDefault(x => x.Element(Cal + "Deleted")?.Value == "1");
		Assert.NotNull(exception);
		Assert.Equal(EasDateTime.ToCompact(secondOccurrence),
			exception.Element(Cal + "ExceptionStartTime")?.Value);
	}

	[BackendFact]
	public async Task Drafts_AddPullSend_RoundTrips()
	{
		EasTestClient sender = gateway.CreateEasClient(TestBackend.User1);
		sender.ProtocolVersion = "16.1";
		await sender.HandshakeAsync();
		string marker = $"DR{Guid.NewGuid():N}"[..10];
		string drafts = sender.FolderOfType(EasFolderType.Drafts).ServerId;
		await sender.InitialSyncAsync(drafts);
		await sender.PullAllAsync(drafts);

		// Create a draft via Sync Add.
		SyncResult add = await sender.AddItemAsync(drafts, "d1",
			new XElement(E + "To", TestBackend.User2),
			new XElement(E + "Subject", $"Draft {marker}"),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "written on the phone, finished on the couch")));
		Assert.Equal("1", add.Status);
		string draftServerId = AddedServerId(add);

		// A second 16.1 device of the same user receives it flagged as a draft.
		EasTestClient observer = gateway.CreateEasClient(TestBackend.User1);
		observer.ProtocolVersion = "16.1";
		await observer.HandshakeAsync();
		string drafts2 = observer.FolderOfType(EasFolderType.Drafts).ServerId;
		await observer.InitialSyncAsync(drafts2);
		SyncItem pulled = await WaitUntil.ResultAsync(async () =>
				(await observer.PullAllAsync(drafts2)).Adds
				.FirstOrDefault(a => a.ApplicationData.Element(E + "Subject")?.Value == $"Draft {marker}"),
			"draft on the second device");
		Assert.Equal("1", pulled.ApplicationData.Element(E2 + "IsDraft")?.Value);

		// Change + email2:Send submits it; the recipient's INBOX proves the delivery.
		SyncResult send = await sender.SyncAsync(drafts, new XElement(AS + "Commands",
			new XElement(AS + "Change",
				new XElement(AS + "ServerId", draftServerId),
				new XElement(E2 + "Send"),
				new XElement(AS + "ApplicationData",
					new XElement(ASB + "Body",
						new XElement(ASB + "Type", "1"),
						new XElement(ASB + "Data", "final version, sent via email2:Send"))))));
		Assert.Equal("1", send.Status);

		await WaitUntil.TrueAsync(
			() => MessageDeliveredAsync(TestBackend.User2, $"Draft {marker}"),
			$"draft '{marker}' delivered to {TestBackend.User2}");
	}

	[BackendFact]
	public async Task CalendarAttachments_UploadSyncAndFetch_RoundTrip()
	{
		byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9];
		EasTestClient client = gateway.CreateLocalStoresEasClient(TestBackend.User2);
		client.ProtocolVersion = "16.1";
		await client.HandshakeAsync();
		string marker = $"CA{Guid.NewGuid():N}"[..10];
		string calendar = client.FolderOfType(EasFolderType.Calendar).ServerId;
		await client.InitialSyncAsync(calendar);
		await client.PullAllAsync(calendar);

		DateTime start = DateTime.UtcNow.Date.AddDays(5).AddHours(13);
		SyncResult add = await client.AddItemAsync(calendar, "ca1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddHours(1))),
			new XElement(Cal + "Subject", $"With agenda {marker}"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"),
			new XElement(ASB + "Attachments",
				new XElement(ASB + "Add",
					new XElement(ASB + "ClientId", "att1"),
					new XElement(ASB + "DisplayName", "agenda.txt"),
					new XElement(ASB + "ContentType", "text/plain"),
					new XElement(ASB + "Content", Convert.ToBase64String(payload)))));
		Assert.Equal("1", add.Status);

		EasTestClient observer = gateway.CreateLocalStoresEasClient(TestBackend.User2);
		observer.ProtocolVersion = "16.1";
		await observer.HandshakeAsync();
		string calendar2 = observer.FolderOfType(EasFolderType.Calendar).ServerId;
		await observer.InitialSyncAsync(calendar2);
		SyncItem item = await WaitUntil.ResultAsync(async () =>
				(await observer.PullAllAsync(calendar2)).Adds
				.FirstOrDefault(a => a.ApplicationData.Element(Cal + "Subject")?.Value == $"With agenda {marker}"),
			"event with attachment metadata");
		XElement? attachment = item.ApplicationData
			.Element(ASB + "Attachments")?.Element(ASB + "Attachment");
		Assert.NotNull(attachment);
		Assert.Equal("agenda.txt", attachment.Element(ASB + "DisplayName")?.Value);
		string fileReference = attachment.Element(ASB + "FileReference")!.Value;
		Assert.StartsWith("calatt::", fileReference, StringComparison.Ordinal);

		XDocument? fetched = await client.PostAsync("ItemOperations", new XDocument(
			new XElement(IO + "ItemOperations",
				new XElement(IO + "Fetch",
					new XElement(IO + "Store", "Mailbox"),
					new XElement(ASB + "FileReference", fileReference)))));
		XElement? fetch = fetched?.Descendants(IO + "Fetch").FirstOrDefault();
		Assert.Equal("1", fetch?.Element(IO + "Status")?.Value);
		string? data = fetch?.Element(IO + "Properties")?.Element(IO + "Data")?.Value;
		Assert.NotNull(data);
		Assert.Equal(payload, Convert.FromBase64String(data));
	}

	[BackendFact]
	public async Task Find_SearchesGal_AndMailbox()
	{
		EasTestClient client = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		client.ProtocolVersion = "16.1";
		await client.HandshakeAsync();
		string marker = $"FN{Guid.NewGuid():N}"[..10];

		string contacts = client.FolderOfType(EasFolderType.Contacts).ServerId;
		await client.InitialSyncAsync(contacts);
		await client.PullAllAsync(contacts);
		Assert.Equal("1", (await client.AddItemAsync(contacts, "f1",
			new XElement(EasNamespaces.Contacts + "FirstName", "Findable"),
			new XElement(EasNamespaces.Contacts + "LastName", marker),
			new XElement(EasNamespaces.Contacts + "Email1Address", $"{marker.ToLowerInvariant()}@example.com"))).Status);

		XDocument? gal = await client.PostAsync("Find", new XDocument(
			new XElement(F + "Find",
				new XElement(F + "SearchId", Guid.NewGuid().ToString()),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "GalSearchCriterion",
						new XElement(F + "Query", new XElement(F + "FreeText", marker)),
						new XElement(F + "Options", new XElement(F + "Range", "0-9")))))));
		Assert.Equal("1", gal?.Root?.Element(F + "Status")?.Value);
		Assert.Contains(gal!.Descendants(EasNamespaces.Gal + "LastName"), e => e.Value == marker);

		// Mailbox Find over the whole mailbox for the warmup-style seeded message.
		XDocument? mailbox = await client.PostAsync("Find", new XDocument(
			new XElement(F + "Find",
				new XElement(F + "SearchId", Guid.NewGuid().ToString()),
				new XElement(F + "ExecuteSearch",
					new XElement(F + "MailBoxSearchCriterion",
						new XElement(F + "Query", new XElement(F + "FreeText", "zzz-no-such-mail-zzz")),
						new XElement(F + "Options",
							new XElement(F + "Range", "0-9"),
							new XElement(F + "DeepTraversal")))))));
		Assert.Equal("1", mailbox?.Root?.Element(F + "Status")?.Value);
		Assert.Equal("1", mailbox?.Root?.Element(F + "Response")?.Element(F + "Status")?.Value);
	}

	[BackendFact]
	public async Task AccountOnlyWipe_DeliversDirective_ThenBlocksAfterAck()
	{
		XNamespace pv = EasNamespaces.Provision;
		string dbPath = Path.Combine(Path.GetTempPath(), $"activesync-wipe-{Guid.NewGuid():N}.db");
		try
		{
			await using Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory =
				gateway.CreateIsolatedFactory(new Dictionary<string, string?>
				{
					["ActiveSync:Database:ConnectionString"] = $"Data Source={dbPath}"
				});
			EasTestClient device = new(
				factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
				{
					AllowAutoRedirect = false
				}),
				TestBackend.User1, TestBackend.Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant())
			{
				ProtocolVersion = "16.1"
			};
			await device.HandshakeAsync();

			// Arm the wipe — exactly what 'eas device wipe' writes.
			await using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={dbPath}"))
			{
				await connection.OpenAsync();
				await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
				command.CommandText = "UPDATE Devices SET PendingAccountWipe = 1 WHERE DeviceId = $device";
				command.Parameters.AddWithValue("$device", device.DeviceId);
				Assert.Equal(1, await command.ExecuteNonQueryAsync());
			}

			// Any non-Provision request is herded into the handshake.
			using HttpResponseMessage herded = await device.PostRawAsync("FolderSync",
				new XDocument(new XElement(EasNamespaces.FolderHierarchy + "FolderSync",
					new XElement(EasNamespaces.FolderHierarchy + "SyncKey", "0"))));
			Assert.Equal((HttpStatusCode)449, herded.StatusCode);

			// Provision delivers the account-only directive…
			XDocument? directive = await device.PostAsync("Provision", new XDocument(
				new XElement(pv + "Provision",
					new XElement(pv + "Policies",
						new XElement(pv + "Policy",
							new XElement(pv + "PolicyType", "MS-EAS-Provisioning-WBXML"))))));
			Assert.NotNull(directive?.Root?.Element(pv + "AccountOnlyRemoteWipe"));

			// …the acknowledgment completes it and the partnership is blocked (403).
			XDocument? ack = await device.PostAsync("Provision", new XDocument(
				new XElement(pv + "Provision",
					new XElement(pv + "AccountOnlyRemoteWipe",
						new XElement(pv + "Status", "1")))));
			Assert.Equal("1", ack?.Root?.Element(pv + "Status")?.Value);
			Assert.Null(ack?.Root?.Element(pv + "AccountOnlyRemoteWipe"));

			using HttpResponseMessage blocked = await device.PostRawAsync("FolderSync",
				new XDocument(new XElement(EasNamespaces.FolderHierarchy + "FolderSync",
					new XElement(EasNamespaces.FolderHierarchy + "SyncKey", "0"))));
			Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
		}
		finally
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

	private static string AddedServerId(SyncResult result)
	{
		XElement? add = result.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.NotNull(add);
		Assert.Equal("1", add.Element(AS + "Status")?.Value);
		string serverId = add.Element(AS + "ServerId")?.Value ?? "";
		Assert.False(string.IsNullOrEmpty(serverId));
		return serverId;
	}

	private static async Task<bool> MessageDeliveredAsync(string user, string subject)
	{
		using ImapClient imap = new();
		await imap.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None);
		await imap.AuthenticateAsync(user, TestBackend.Password);
		await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
		IList<UniqueId> hits = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
		await imap.DisconnectAsync(true);
		return hits.Count > 0;
	}
}
