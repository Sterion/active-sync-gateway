using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Endpoint resolution for the OPT-IN live suite against a real Axigen server. Runs only
///   when AS_TEST_AXIGEN_HOST + AS_TEST_AXIGEN_USER + AS_TEST_AXIGEN_PASSWORD are all set
///   (a dedicated throwaway account — never a real mailbox). Host and credentials
///   deliberately never appear in the repo.
/// </summary>
public static class AxigenBackend
{
	public static string? Host { get; } = Environment.GetEnvironmentVariable("AS_TEST_AXIGEN_HOST");
	public static string? User { get; } = Environment.GetEnvironmentVariable("AS_TEST_AXIGEN_USER");
	public static string? Password { get; } = Environment.GetEnvironmentVariable("AS_TEST_AXIGEN_PASSWORD");

	public static bool IsConfigured =>
		Host is { Length: > 0 } && User is { Length: > 0 } && Password is { Length: > 0 };
}

/// <summary>A [Fact] that runs only when the Axigen live account is configured.</summary>
public sealed class AxigenFactAttribute : FactAttribute
{
	public AxigenFactAttribute()
	{
		if (!AxigenBackend.IsConfigured)
			Skip = "Axigen live tests need AS_TEST_AXIGEN_HOST + AS_TEST_AXIGEN_USER + AS_TEST_AXIGEN_PASSWORD.";
	}
}

/// <summary>
///   Hosts the gateway in-process against the Axigen server: IMAP 993 (SSL), SMTP 465 (SSL),
///   CalDAV home set /Calendar/ and CardDAV home set /Contacts/ over plain HTTP port 80
///   (Axigen only reports each home set from its own root, so both are pinned explicitly).
/// </summary>
public sealed class AxigenGatewayFixture : IAsyncLifetime
{
	private readonly string _dbPath =
		Path.Combine(Path.GetTempPath(), $"activesync-axigen-{Guid.NewGuid():N}.db");

	private WebApplicationFactory<Program>? _factory;

	public WebApplicationFactory<Program> Factory => _factory ??= CreateFactory();

	public Task InitializeAsync()
	{
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		if (_factory is not null)
			await _factory.DisposeAsync();
		await SweepLeftoverTestMailAsync();
		try
		{
			File.Delete(_dbPath);
		}
		catch (IOException)
		{
			// still locked on Windows — temp files get cleaned eventually
		}
	}

	/// <summary>
	///   Deletes any "Draft AXDR…" test mails from every IMAP folder, so a mid-test failure
	///   never leaves clutter behind in the shared test mailbox. Best-effort by design.
	/// </summary>
	private static async Task SweepLeftoverTestMailAsync()
	{
		if (!AxigenBackend.IsConfigured)
			return;
		try
		{
			using ImapClient imap = new();
			await imap.ConnectAsync(AxigenBackend.Host!, 993, SecureSocketOptions.SslOnConnect);
			await imap.AuthenticateAsync(AxigenBackend.User!, AxigenBackend.Password!);
			foreach (IMailFolder folder in await imap.GetFoldersAsync(imap.PersonalNamespaces[0]))
			{
				try
				{
					await folder.OpenAsync(FolderAccess.ReadWrite);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					continue; // \Noselect and friends
				}

				IList<UniqueId> leftovers =
					await folder.SearchAsync(SearchQuery.SubjectContains("Draft AXDR"));
				if (leftovers.Count > 0)
				{
					await folder.AddFlagsAsync(leftovers, MessageFlags.Deleted, true);
					await folder.ExpungeAsync();
				}

				await folder.CloseAsync();
			}

			await imap.DisconnectAsync(true);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Cleanup must never fail the run.
			Console.WriteLine($"Axigen leftover sweep skipped: {ex.Message}");
		}
	}

	public EasTestClient CreateEasClient(string protocolVersion = "14.1")
	{
		return new EasTestClient(
			Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			AxigenBackend.User!, AxigenBackend.Password!,
			$"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant())
		{
			ProtocolVersion = protocolVersion
		};
	}

	private WebApplicationFactory<Program> CreateFactory()
	{
		Dictionary<string, string?> settings = new()
		{
			["ActiveSync:Imap:Host"] = AxigenBackend.Host,
			["ActiveSync:Imap:Port"] = "993",
			["ActiveSync:Imap:UseSsl"] = "true",
			["ActiveSync:Smtp:Host"] = AxigenBackend.Host,
			["ActiveSync:Smtp:Port"] = "465",
			["ActiveSync:Smtp:UseSsl"] = "true",
			["ActiveSync:CalDav:BaseUrl"] = $"http://{AxigenBackend.Host}",
			["ActiveSync:CalDav:HomeSetPath"] = "/Calendar/",
			["ActiveSync:CardDav:BaseUrl"] = $"http://{AxigenBackend.Host}",
			["ActiveSync:CardDav:HomeSetPath"] = "/Contacts/",
			["ActiveSync:Database:ConnectionString"] = $"Data Source={_dbPath}",
			["ActiveSync:Encryption:Key"] = GatewayFixture.TestEncryptionKey,
			["ActiveSync:Eas:MinHeartbeatSeconds"] = "5",
			["ActiveSync:Eas:MaxHeartbeatSeconds"] = "120",
			["ActiveSync:Eas:DavPollSeconds"] = "5",
			["ActiveSync:Auth:MaxFailures"] = "1000000"
		};

		return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			HostingAbstractionsWebHostBuilderExtensions
				.UseEnvironment(builder, Environments.Production);
			builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
		});
	}
}

[CollectionDefinition("axigen")]
public class AxigenCollection : ICollectionFixture<AxigenGatewayFixture>;

/// <summary>
///   Live verification of the feature/eas-16-uplift branch against Axigen: 16.1 negotiation,
///   drafts sync + email2:Send, calendar 16.x occurrence deletes, inline event attachments,
///   GAL photos from CardDAV (incl. the Axigen Directory address book) and free/busy via
///   CALDAV:free-busy-query. ManageSieve (Oof) is NOT covered — Axigen has no port-4190
///   listener. Every test deletes what it created; the account is a dedicated test mailbox.
/// </summary>
[Collection("axigen")]
[Trait("Category", "AxigenLive")]
public sealed class AxigenLiveTests(AxigenGatewayFixture axigen)
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace C = EasNamespaces.Contacts;
	private static readonly XNamespace E = EasNamespaces.Email;
	private static readonly XNamespace E2 = EasNamespaces.Email2;
	private static readonly XNamespace F = EasNamespaces.Find;
	private static readonly XNamespace S = EasNamespaces.Search;
	private static readonly XNamespace RR = EasNamespaces.ResolveRecipients;
	private static readonly XNamespace Gal = EasNamespaces.Gal;
	private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 9, 8, 7, 6, 5];

	[AxigenFact]
	public async Task Handshake_Advertises161_AndFindsAllFolderKinds()
	{
		EasTestClient client = axigen.CreateEasClient("16.1");
		using HttpResponseMessage options = await client.OptionsAsync();
		string versions = options.Headers.GetValues("MS-ASProtocolVersions").Single();
		Assert.Contains("16.1", versions);
		Assert.Contains("Find", options.Headers.GetValues("MS-ASProtocolCommands").Single());

		await client.HandshakeAsync();
		Assert.NotNull(client.FolderOfType(EasFolderType.Inbox));
		Assert.NotNull(client.FolderOfType(EasFolderType.Drafts));
		Assert.NotNull(client.FolderOfType(EasFolderType.Calendar));
		Assert.NotNull(client.FolderOfType(EasFolderType.Contacts));
	}

	[AxigenFact]
	public async Task Drafts_AddPullSend_RoundTripsThroughAxigen()
	{
		EasTestClient sender = axigen.CreateEasClient("16.1");
		await sender.HandshakeAsync();
		string marker = $"AXDR{Guid.NewGuid():N}"[..12];
		string drafts = sender.FolderOfType(EasFolderType.Drafts).ServerId;
		string inbox = sender.FolderOfType(EasFolderType.Inbox).ServerId;
		await sender.InitialSyncAsync(drafts);
		await sender.PullAllAsync(drafts);
		await sender.InitialSyncAsync(inbox);
		await sender.PullAllAsync(inbox);

		SyncResult add = await sender.AddItemAsync(drafts, "axd1",
			new XElement(E + "To", AxigenBackend.User!),
			new XElement(E + "Subject", $"Draft {marker}"),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "drafted against a real Axigen")));
		Assert.Equal("1", add.Status);
		string draftServerId = AddedServerId(add);

		// A second device re-reads the draft from Axigen's Drafts folder over IMAP.
		EasTestClient observer = axigen.CreateEasClient("16.1");
		await observer.HandshakeAsync();
		string drafts2 = observer.FolderOfType(EasFolderType.Drafts).ServerId;
		await observer.InitialSyncAsync(drafts2);
		SyncItem pulled = await WaitUntil.ResultAsync(async () =>
				(await observer.PullAllAsync(drafts2)).Adds
				.FirstOrDefault(a => a.ApplicationData.Element(E + "Subject")?.Value == $"Draft {marker}"),
			"draft on the second device");
		Assert.Equal("1", pulled.ApplicationData.Element(E2 + "IsDraft")?.Value);

		// Change + email2:Send submits via Axigen SMTP; the account mails itself.
		SyncResult send = await sender.SyncAsync(drafts, new XElement(AS + "Commands",
			new XElement(AS + "Change",
				new XElement(AS + "ServerId", draftServerId),
				new XElement(E2 + "Send"),
				new XElement(AS + "ApplicationData",
					new XElement(ASB + "Body",
						new XElement(ASB + "Type", "1"),
						new XElement(ASB + "Data", "sent via email2:Send through Axigen"))))));
		Assert.Equal("1", send.Status);

		SyncItem delivered = await WaitUntil.ResultAsync(async () =>
				(await sender.PullAllAsync(inbox)).Adds
				.FirstOrDefault(a => a.ApplicationData.Element(E + "Subject")?.Value == $"Draft {marker}"),
			"self-addressed mail in the INBOX", TimeSpan.FromSeconds(90));

		// Cleanup: the delivered copy, the filed Sent copy, plus any draft remnant
		// (hard deletes, no Trash).
		await sender.DeleteItemAsync(inbox, delivered.ServerId, false);
		string sent = sender.FolderOfType(EasFolderType.SentItems).ServerId;
		await sender.InitialSyncAsync(sent);
		SyncResult sentItems = await sender.PullAllAsync(sent);
		foreach (SyncItem copy in sentItems.Adds
			         .Where(i => i.ApplicationData.Element(E + "Subject")?.Value == $"Draft {marker}"))
			await sender.DeleteItemAsync(sent, copy.ServerId, false);
		SyncResult draftsLeft = await sender.PullAllAsync(drafts);
		foreach (SyncItem leftover in draftsLeft.Adds.Concat(draftsLeft.Changes)
			         .Where(i => i.ApplicationData.Element(E + "Subject")?.Value == $"Draft {marker}"))
			await sender.DeleteItemAsync(drafts, leftover.ServerId, false);
	}

	[AxigenFact]
	public async Task Calendar16_InstanceIdDelete_CancelsOneOccurrence_OnAxigenCalDav()
	{
		EasTestClient writer = axigen.CreateEasClient("16.1");
		await writer.HandshakeAsync();
		string marker = $"AXC{Guid.NewGuid():N}"[..10];
		string calendar = writer.FolderOfType(EasFolderType.Calendar).ServerId;
		await writer.InitialSyncAsync(calendar);
		await writer.PullAllAsync(calendar);

		DateTime start = DateTime.UtcNow.Date.AddDays(3).AddHours(9);
		SyncResult add = await writer.AddItemAsync(calendar, "axc1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "ClientUid", $"axigen-uid-{marker}"),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddHours(1))),
			new XElement(Cal + "Subject", $"Standup {marker}"),
			new XElement(ASB + "Location", new XElement(ASB + "DisplayName", "Axigen war room")),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "0"),
				new XElement(Cal + "Interval", "1"),
				new XElement(Cal + "Occurrences", "5")),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"));
		Assert.Equal("1", add.Status);
		string serverId = AddedServerId(add);
		try
		{
			// A second device re-reads the event from Axigen; 16.1 gets structured Location.
			EasTestClient observer = axigen.CreateEasClient("16.1");
			await observer.HandshakeAsync();
			string calendar2 = observer.FolderOfType(EasFolderType.Calendar).ServerId;
			await observer.InitialSyncAsync(calendar2);
			SyncItem seen = await WaitUntil.ResultAsync(async () =>
					(await observer.PullAllAsync(calendar2)).Adds
					.FirstOrDefault(a => a.ApplicationData.Element(Cal + "Subject")?.Value == $"Standup {marker}"),
				"event on the second device");
			Assert.Equal("Axigen war room",
				seen.ApplicationData.Element(ASB + "Location")?.Element(ASB + "DisplayName")?.Value);

			DateTime secondOccurrence = start.AddDays(1);
			SyncResult occurrenceDelete = await writer.SyncAsync(calendar, new XElement(AS + "Commands",
				new XElement(AS + "Delete",
					new XElement(AS + "ServerId", serverId),
					new XElement(ASB + "InstanceId",
						secondOccurrence.ToString("yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'",
							System.Globalization.CultureInfo.InvariantCulture)))));
			Assert.Equal("1", occurrenceDelete.Status);

			SyncItem afterDelete = await WaitUntil.ResultAsync(async () =>
					(await observer.PullAllAsync(calendar2)).Changes
					.FirstOrDefault(c => c.ApplicationData.Element(Cal + "Subject")?.Value == $"Standup {marker}"),
				"event with the cancelled occurrence");
			XElement? exception = afterDelete.ApplicationData
				.Element(Cal + "Exceptions")?.Elements(Cal + "Exception")
				.FirstOrDefault(x => x.Element(Cal + "Deleted")?.Value == "1");
			Assert.NotNull(exception);
			Assert.Equal(EasDateTime.ToCompact(secondOccurrence),
				exception.Element(Cal + "ExceptionStartTime")?.Value);
		}
		finally
		{
			await writer.DeleteItemAsync(calendar, serverId, false);
		}
	}

	[AxigenFact]
	public async Task CalendarAttachments_InlineAttach_RoundTripsOnAxigen()
	{
		byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9];
		EasTestClient writer = axigen.CreateEasClient("16.1");
		await writer.HandshakeAsync();
		string marker = $"AXA{Guid.NewGuid():N}"[..10];
		string calendar = writer.FolderOfType(EasFolderType.Calendar).ServerId;
		await writer.InitialSyncAsync(calendar);
		await writer.PullAllAsync(calendar);

		DateTime start = DateTime.UtcNow.Date.AddDays(5).AddHours(13);
		SyncResult add = await writer.AddItemAsync(calendar, "axa1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddHours(1))),
			new XElement(Cal + "Subject", $"With agenda {marker}"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"),
			new XElement(ASB + "Attachments",
				new XElement(ASB + "Add",
					new XElement(ASB + "ClientId", "axatt1"),
					new XElement(ASB + "DisplayName", "agenda.txt"),
					new XElement(ASB + "ContentType", "text/plain"),
					new XElement(ASB + "Content", Convert.ToBase64String(payload)))));
		Assert.Equal("1", add.Status);
		string serverId = AddedServerId(add);
		try
		{
			// The attachment must survive Axigen's ICS rewriting: a second device re-reads it.
			EasTestClient observer = axigen.CreateEasClient("16.1");
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

			(string status, string? contentType, byte[]? data) =
				await observer.FetchAttachmentAsync(fileReference);
			Assert.Equal("1", status);
			Assert.Equal(payload, data);
		}
		finally
		{
			await writer.DeleteItemAsync(calendar, serverId, false);
		}
	}

	[AxigenFact]
	public async Task FreeBusy_OwnAvailability_ViaAxigenFreeBusyQuery()
	{
		EasTestClient client = axigen.CreateEasClient("14.1");
		await client.HandshakeAsync();
		string calendar = client.FolderOfType(EasFolderType.Calendar).ServerId;
		await client.InitialSyncAsync(calendar);
		await client.PullAllAsync(calendar);

		// Busy 10:00–11:00 UTC on a fixed future day.
		DateTime day = DateTime.UtcNow.Date.AddDays(10);
		SyncResult add = await client.AddItemAsync(calendar, "axfb1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(day.AddHours(10))),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(day.AddHours(11))),
			new XElement(Cal + "Subject", "Axigen busy hour"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"));
		Assert.Equal("1", add.Status);
		string serverId = AddedServerId(add);
		try
		{
			string merged = await WaitUntil.ResultAsync(async () =>
				{
					XDocument? response = await client.PostAsync("ResolveRecipients", new XDocument(
						new XElement(RR + "ResolveRecipients",
							new XElement(RR + "To", AxigenBackend.User!),
							new XElement(RR + "Options",
								new XElement(RR + "Availability",
									new XElement(RR + "StartTime", day.ToString(
										"yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'",
										System.Globalization.CultureInfo.InvariantCulture)),
									new XElement(RR + "EndTime", day.AddDays(1).ToString(
										"yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'",
										System.Globalization.CultureInfo.InvariantCulture)))))));
					XElement? recipient = response?.Descendants(RR + "Recipient")
						.FirstOrDefault(r => string.Equals(r.Element(RR + "EmailAddress")?.Value,
							AxigenBackend.User, StringComparison.OrdinalIgnoreCase));
					XElement? availability = recipient?.Element(RR + "Availability");
					if (availability?.Element(RR + "Status")?.Value != "1")
						return null;
					string? digits = availability.Element(RR + "MergedFreeBusy")?.Value;
					// Axigen may lag the free/busy view a little — retry until busy shows.
					return digits is not null && digits.Contains('2') ? digits : null;
				},
				"own MergedFreeBusy digits with the busy block", TimeSpan.FromSeconds(60));

			Assert.Equal(48, merged.Length); // 24 h / 30 min
			Assert.Equal('2', merged[20]); // 10:00–10:30
			Assert.Equal('2', merged[21]); // 10:30–11:00
			Assert.Equal('0', merged[0]);
		}
		finally
		{
			await client.DeleteItemAsync(calendar, serverId, false);
		}
	}

	[AxigenFact]
	public async Task GalPhotos_SearchAndResolveRecipients_FromAxigenCardDav()
	{
		EasTestClient client = axigen.CreateEasClient("14.1");
		await client.HandshakeAsync();
		string marker = $"AXGP{Guid.NewGuid():N}"[..12];
		string address = $"{marker.ToLowerInvariant()}@example.com";
		string contacts = client.FolderOfType(EasFolderType.Contacts).ServerId;
		await client.InitialSyncAsync(contacts);
		await client.PullAllAsync(contacts);

		SyncResult add = await client.AddItemAsync(contacts, "axgp1",
			new XElement(C + "FirstName", "Photo"),
			new XElement(C + "LastName", marker),
			new XElement(C + "Email1Address", address),
			new XElement(C + "Picture", Convert.ToBase64String(PhotoBytes)));
		Assert.Equal("1", add.Status);
		string serverId = AddedServerId(add);
		try
		{
			// Search GAL with a photo request — the photo must survive Axigen's vCard storage.
			// Axigen indexes new DAV items asynchronously (listings lag PUTs), so poll.
			XElement result = await WaitUntil.ResultAsync(async () =>
				{
					XDocument? search = await client.PostAsync("Search", new XDocument(
						new XElement(S + "Search",
							new XElement(S + "Store",
								new XElement(S + "Name", "GAL"),
								new XElement(S + "Query", marker),
								new XElement(S + "Options",
									new XElement(S + "Range", "0-9"),
									new XElement(S + "Picture",
										new XElement(S + "MaxSize", "65536"),
										new XElement(S + "MaxPictures", "5")))))));
					return search?.Descendants(S + "Result")
						.FirstOrDefault(r => r.Descendants(Gal + "LastName").Any(e => e.Value == marker));
				},
				"GAL search hit for the new contact", TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(5));
			XElement? galPicture = result.Descendants(Gal + "Picture").FirstOrDefault();
			Assert.NotNull(galPicture);
			Assert.Equal("1", galPicture.Element(Gal + "Status")?.Value);
			Assert.Equal(PhotoBytes, Convert.FromBase64String(galPicture.Element(Gal + "Data")!.Value));

			// ResolveRecipients resolves the same contact with its photo.
			XDocument? resolved = await client.PostAsync("ResolveRecipients", new XDocument(
				new XElement(RR + "ResolveRecipients",
					new XElement(RR + "To", marker),
					new XElement(RR + "Options",
						new XElement(RR + "Picture",
							new XElement(RR + "MaxSize", "65536"),
							new XElement(RR + "MaxPictures", "3"))))));
			XElement? recipient = resolved?.Descendants(RR + "Recipient")
				.FirstOrDefault(r => r.Element(RR + "EmailAddress")?.Value == address);
			Assert.NotNull(recipient);
			Assert.Equal("1", recipient.Element(RR + "Picture")?.Element(RR + "Status")?.Value);
		}
		finally
		{
			await client.DeleteItemAsync(contacts, serverId, false);
		}
	}

	[AxigenFact]
	public async Task Find_SearchesGalAndMailbox_OnAxigen()
	{
		EasTestClient client = axigen.CreateEasClient("16.1");
		await client.HandshakeAsync();
		string marker = $"AXFN{Guid.NewGuid():N}"[..12];
		string contacts = client.FolderOfType(EasFolderType.Contacts).ServerId;
		await client.InitialSyncAsync(contacts);
		await client.PullAllAsync(contacts);

		SyncResult add = await client.AddItemAsync(contacts, "axfn1",
			new XElement(C + "FirstName", "Findable"),
			new XElement(C + "LastName", marker),
			new XElement(C + "Email1Address", $"{marker.ToLowerInvariant()}@example.com"));
		Assert.Equal("1", add.Status);
		string serverId = AddedServerId(add);
		try
		{
			// Axigen indexes new DAV items asynchronously (listings lag PUTs), so poll.
			XDocument gal = await WaitUntil.ResultAsync(async () =>
				{
					XDocument? found = await client.PostAsync("Find", new XDocument(
						new XElement(F + "Find",
							new XElement(F + "SearchId", Guid.NewGuid().ToString()),
							new XElement(F + "ExecuteSearch",
								new XElement(F + "GalSearchCriterion",
									new XElement(F + "Query", new XElement(F + "FreeText", marker)),
									new XElement(F + "Options", new XElement(F + "Range", "0-9")))))));
					return found?.Descendants(Gal + "LastName").Any(e => e.Value == marker) == true
						? found
						: null;
				},
				"Find GAL hit for the new contact", TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(5));
			Assert.Equal("1", gal.Root?.Element(F + "Status")?.Value);

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
		finally
		{
			await client.DeleteItemAsync(contacts, serverId, false);
		}
	}

	[AxigenFact]
	public async Task RecurringTask_RoundTrips_OnAxigenVtodoCollection()
	{
		EasTestClient writer = axigen.CreateEasClient("14.1");
		await writer.HandshakeAsync();
		EasFolder? tasksFolder = writer.Folders.FirstOrDefault(f => f.Type == EasFolderType.Tasks);
		Assert.NotNull(tasksFolder); // Axigen ships /Calendar/Tasks/ out of the box

		string marker = $"AXRT{Guid.NewGuid():N}"[..12];
		await writer.InitialSyncAsync(tasksFolder.ServerId);
		await writer.PullAllAsync(tasksFolder.ServerId);
		string startDate = EasDateTime.ToLong(DateTime.UtcNow.Date.AddDays(1));
		SyncResult add = await writer.AddItemAsync(tasksFolder.ServerId, "axrt1",
			new XElement(EasNamespaces.Tasks + "Subject", marker),
			new XElement(EasNamespaces.Tasks + "Complete", "0"),
			new XElement(EasNamespaces.Tasks + "StartDate", startDate),
			new XElement(EasNamespaces.Tasks + "Recurrence",
				new XElement(EasNamespaces.Tasks + "Type", "1"),
				new XElement(EasNamespaces.Tasks + "Start", startDate),
				new XElement(EasNamespaces.Tasks + "DayOfWeek", "62")));
		Assert.Equal("1", add.Status);
		string serverId = AddedServerId(add);
		try
		{
			// The RRULE must survive Axigen's VTODO storage: a second device re-reads it.
			EasTestClient observer = axigen.CreateEasClient("14.1");
			await observer.HandshakeAsync();
			string tasks2 = observer.FolderOfType(EasFolderType.Tasks).ServerId;
			await observer.InitialSyncAsync(tasks2);
			SyncItem received = await WaitUntil.ResultAsync(async () =>
					(await observer.PullAllAsync(tasks2)).Adds.FirstOrDefault(a =>
						a.ApplicationData.Element(EasNamespaces.Tasks + "Subject")?.Value == marker),
				"recurring task on the second device", TimeSpan.FromSeconds(120));
			XElement? recurrence = received.ApplicationData.Element(EasNamespaces.Tasks + "Recurrence");
			Assert.NotNull(recurrence);
			Assert.Equal("1", recurrence.Element(EasNamespaces.Tasks + "Type")?.Value);
			Assert.Equal("62", recurrence.Element(EasNamespaces.Tasks + "DayOfWeek")?.Value);
		}
		finally
		{
			await writer.DeleteItemAsync(tasksFolder.ServerId, serverId, false);
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
}
