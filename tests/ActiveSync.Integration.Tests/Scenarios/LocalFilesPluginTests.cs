using System.Net;
using System.Xml.Linq;
using ActiveSync.Core.Security;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The filesystem plugin (<c>tests/ActiveSync.Plugin.Local</c>) driving a REAL gateway end to
///   end: every role — mail, submission, calendar, tasks, contacts and notes — served by an
///   out-of-repo-shaped plugin out of a directory tree, with no mail server, no DAV server and no
///   docker anywhere.
///   <para>
///     These are plain <c>[Fact]</c>s rather than <c>[BackendFact]</c>s on purpose: this is the one
///     integration lane that needs no backend stack at all, so it runs on any machine and in every
///     CI leg. It is also the end-to-end proof of the plugin contract — a phone's whole session
///     against an assembly that references only <c>ActiveSync.Contracts</c>.
///   </para>
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class LocalFilesPluginTests(GatewayFixture gateway) : IAsyncLifetime
{
	private const string PluginName = "ActiveSync.Plugin.Local";
	private const string Login = "plugin1@example.com";
	private const string Password = "plugin-pa55!";

	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;
	private static readonly XNamespace C = EasNamespaces.Contacts;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace E = EasNamespaces.Email;
	private static readonly XNamespace E2 = EasNamespaces.Email2;
	private static readonly XNamespace N = EasNamespaces.Notes;
	private static readonly XNamespace T = EasNamespaces.Tasks;

	private static readonly string StagedPluginDll =
		Path.Combine(AppContext.BaseDirectory, "localfilesplugin", PluginName + ".dll");

	private readonly List<WebApplicationFactory<Program>> _factories = [];
	private readonly List<string> _directories = [];

	public Task InitializeAsync()
	{
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		foreach (WebApplicationFactory<Program> factory in _factories)
			await factory.DisposeAsync();
		foreach (string directory in _directories)
			try
			{
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A non-collectible plugin load context keeps the DLL mapped for the process lifetime.
			}
	}

	/// <summary>
	///   A gateway with no external backend at all still offers a phone every content class: the
	///   plugin synthesizes the four mail special folders on an empty directory and one folder per
	///   payload class, and the host takes each folder's type verbatim.
	/// </summary>
	[Fact]
	public async Task FolderSync_ExposesEveryClass_FromAnEmptyDirectory()
	{
		(EasTestClient client, string root) = await StartAsync();

		foreach (int type in (int[])
		         [EasFolderType.Inbox, EasFolderType.Drafts, EasFolderType.SentItems,
			         EasFolderType.DeletedItems, EasFolderType.Calendar, EasFolderType.Contacts,
			         EasFolderType.Tasks, EasFolderType.Notes])
			Assert.Equal(type, client.FolderOfType(type).Type);

		Assert.True(Directory.Exists(Path.Combine(root, "mail", "Inbox")));
		Assert.True(Directory.Exists(Path.Combine(root, "mail", "Sent")));
	}

	/// <summary>
	///   The headline behaviour: drop an <c>.eml</c> into the Inbox directory and it reaches the
	///   phone. Marking it read must then NOT come back as a change — the store adopts the file
	///   under a minted key and a flag change only renames it, so the host's echo suppression, which
	///   is keyed on the item key, still holds.
	/// </summary>
	[Fact]
	public async Task DroppedMessage_Syncs_AndMarkingItReadDoesNotEchoBack()
	{
		(EasTestClient client, string root) = await StartAsync();
		string marker = $"LF{Guid.NewGuid():N}"[..10];
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		// A file name no store would produce, dropped by hand while the gateway is running.
		await File.WriteAllTextAsync(
			Path.Combine(root, "mail", "Inbox", "Smith, John - invoice.eml"),
			EasTestClient.BuildMime(Login, Login, $"Dropped {marker}", "left here by hand"));

		SyncItem received = await WaitUntil.ResultAsync(async () =>
				(await client.PullAllAsync(inbox)).Adds.FirstOrDefault(add =>
					add.ApplicationData.Element(E + "Subject")?.Value == $"Dropped {marker}"),
			$"dropped message '{marker}'");

		// The item key travels on the wire inside the ServerId, which MS-ASCMD caps at 64 chars —
		// the reason the store mints keys instead of deriving them from the file name.
		Assert.True(received.ServerId.Length <= 64, $"ServerId '{received.ServerId}' is too long");

		SyncResult read = await client.ChangeItemAsync(inbox, received.ServerId, new XElement(E + "Read", "1"));
		Assert.Equal("1", read.Status);

		// The rename that carried the flag must not read as a new item, nor as a change to echo back.
		SyncResult after = await client.PullAllAsync(inbox);
		Assert.DoesNotContain(after.Adds, add => add.ServerId == received.ServerId);
		Assert.DoesNotContain(after.Changes, change => change.ServerId == received.ServerId);
		Assert.Empty((await client.PullAllAsync(inbox)).Changes);
	}

	/// <summary>
	///   One item of every payload class, created on one device and received on another, with the
	///   on-disk files asserted: the payload is stored in its own native format, verbatim.
	/// </summary>
	[Fact]
	public async Task GroupwareItems_RoundTripAcrossDevices_AndLandOnDiskInNativeFormats()
	{
		(EasTestClient device1, string root) = await StartAsync();
		EasTestClient device2 = await SecondDeviceAsync(device1);
		string marker = $"LF{Guid.NewGuid():N}"[..10];

		string contacts1 = device1.FolderOfType(EasFolderType.Contacts).ServerId;
		string calendar1 = device1.FolderOfType(EasFolderType.Calendar).ServerId;
		string tasks1 = device1.FolderOfType(EasFolderType.Tasks).ServerId;
		string notes1 = device1.FolderOfType(EasFolderType.Notes).ServerId;
		foreach (string collection in (string[]) [contacts1, calendar1, tasks1, notes1])
		{
			await device1.InitialSyncAsync(collection);
			await device1.PullAllAsync(collection);
		}

		SyncResult contactAdd = await device1.AddItemAsync(contacts1, "c1",
			new XElement(C + "FirstName", "Files"),
			new XElement(C + "LastName", marker),
			new XElement(C + "Email1Address", "files@example.com"));
		AssertAdded(contactAdd, out string contactServerId);

		DateTime start = DateTime.UtcNow.Date.AddDays(2).AddHours(10);
		AssertAdded(await device1.AddItemAsync(calendar1, "e1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddHours(1))),
			new XElement(Cal + "Subject", $"Meet {marker}"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0")), out _);

		AssertAdded(await device1.AddItemAsync(tasks1, "t1",
			new XElement(T + "Subject", $"Task {marker}"),
			new XElement(T + "Complete", "0"),
			new XElement(T + "Importance", "2")), out string taskServerId);

		AssertAdded(await device1.AddItemAsync(notes1, "n1",
			new XElement(N + "Subject", $"Note {marker}"),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "remember the milk"))), out _);

		// --- the files are there, in the formats the contract hands over ---
		Assert.Contains("BEGIN:VCARD", await SingleFileTextAsync(root, "contacts", "*.vcf"));
		Assert.Contains("BEGIN:VEVENT", await SingleFileTextAsync(root, "calendar", "*.ics"));
		Assert.Contains("BEGIN:VTODO", await SingleFileTextAsync(root, "tasks", "*.ics"));
		Assert.Contains("remember the milk", await SingleFileTextAsync(root, "notes", "*.json"));

		// --- and on the second device ---
		string contacts2 = device2.FolderOfType(EasFolderType.Contacts).ServerId;
		string calendar2 = device2.FolderOfType(EasFolderType.Calendar).ServerId;
		string tasks2 = device2.FolderOfType(EasFolderType.Tasks).ServerId;
		string notes2 = device2.FolderOfType(EasFolderType.Notes).ServerId;
		foreach (string collection in (string[]) [contacts2, calendar2, tasks2, notes2])
			await device2.InitialSyncAsync(collection);

		SyncItem contact = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(contacts2)).Adds.FirstOrDefault(add =>
					add.ApplicationData.Element(C + "LastName")?.Value == marker),
			$"contact '{marker}' on device 2");
		Assert.Equal("Files", contact.ApplicationData.Element(C + "FirstName")?.Value);

		SyncItem meeting = await WaitUntil.ResultAsync(async () =>
				(await device2.PullAllAsync(calendar2)).Adds.FirstOrDefault(add =>
					add.ApplicationData.Element(Cal + "Subject")?.Value == $"Meet {marker}"),
			$"event '{marker}' on device 2");
		Assert.Equal(EasDateTime.ToCompact(start), meeting.ApplicationData.Element(Cal + "StartTime")?.Value);

		await WaitUntil.TrueAsync(async () =>
				(await device2.PullAllAsync(tasks2)).Adds.Any(add =>
					add.ApplicationData.Element(T + "Subject")?.Value == $"Task {marker}"),
			$"task '{marker}' on device 2");
		await WaitUntil.TrueAsync(async () =>
				(await device2.PullAllAsync(notes2)).Adds.Any(add =>
					add.ApplicationData.Element(N + "Subject")?.Value == $"Note {marker}"),
			$"note '{marker}' on device 2");

		// --- a change and a delete propagate too ---
		await device1.ChangeItemAsync(tasks1, taskServerId,
			new XElement(T + "Subject", $"Task {marker}"),
			new XElement(T + "Complete", "1"));
		await WaitUntil.TrueAsync(async () =>
				(await device2.PullAllAsync(tasks2)).Changes.Any(change =>
					change.ApplicationData.Element(T + "Complete")?.Value == "1"),
			"task completion on device 2");

		await device1.DeleteItemAsync(contacts1, contactServerId);
		await WaitUntil.TrueAsync(
			async () => (await device2.PullAllAsync(contacts2)).Deletes.Contains(contact.ServerId),
			"contact deletion on device 2");
		Assert.Empty(Directory.GetFiles(Path.Combine(root, "contacts"), "*.vcf", SearchOption.AllDirectories));
	}

	/// <summary>
	///   There is no MTA behind a directory, so a send loops back into the sender's own Inbox —
	///   and the host's separate save-to-Sent puts the same message in Sent, exactly as it would
	///   against a real backend. Both halves are asserted on the wire AND on disk.
	/// </summary>
	[Fact]
	public async Task SendMail_LoopsBackToTheSendersInbox_AndIsSavedToSent()
	{
		(EasTestClient client, string root) = await StartAsync();
		string marker = $"LF{Guid.NewGuid():N}"[..10];
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		string sent = client.FolderOfType(EasFolderType.SentItems).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.InitialSyncAsync(sent);
		await client.PullAllAsync(inbox);
		await client.PullAllAsync(sent);

		// Success for SendMail is an empty 200, not a WBXML body.
		Assert.Null(await client.SendMailAsync(
			EasTestClient.BuildMime(Login, Login, $"Loop {marker}", "sent from the phone")));

		await WaitUntil.TrueAsync(async () =>
				(await client.PullAllAsync(inbox)).Adds.Any(add =>
					add.ApplicationData.Element(E + "Subject")?.Value == $"Loop {marker}"),
			$"looped-back message '{marker}' in the Inbox");
		await WaitUntil.TrueAsync(async () =>
				(await client.PullAllAsync(sent)).Adds.Any(add =>
					add.ApplicationData.Element(E + "Subject")?.Value == $"Loop {marker}"),
			$"message '{marker}' in Sent");

		Assert.Single(Directory.GetFiles(Path.Combine(root, "mail", "Inbox"), "*.eml"));
		Assert.Single(Directory.GetFiles(Path.Combine(root, "mail", "Sent"), "*.eml"));
	}

	/// <summary>
	///   A client delete is a move to Trash by default and an unlink when the client asks for a hard
	///   delete — the one place mail's <c>permanent</c> flag means something.
	/// </summary>
	[Fact]
	public async Task DeletedMessage_MovesToTrash_ThenDeletesPermanently()
	{
		(EasTestClient client, string root) = await StartAsync();
		string marker = $"LF{Guid.NewGuid():N}"[..10];
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		string trash = client.FolderOfType(EasFolderType.DeletedItems).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.InitialSyncAsync(trash);
		await client.PullAllAsync(inbox);
		await client.PullAllAsync(trash);

		await File.WriteAllTextAsync(
			Path.Combine(root, "mail", "Inbox", "doomed.eml"),
			EasTestClient.BuildMime(Login, Login, $"Doomed {marker}", "not long for this world"));

		SyncItem message = await WaitUntil.ResultAsync(async () =>
				(await client.PullAllAsync(inbox)).Adds.FirstOrDefault(add =>
					add.ApplicationData.Element(E + "Subject")?.Value == $"Doomed {marker}"),
			$"message '{marker}'");

		await client.DeleteItemAsync(inbox, message.ServerId);
		Assert.Empty(Directory.GetFiles(Path.Combine(root, "mail", "Inbox"), "*.eml"));
		Assert.Single(Directory.GetFiles(Path.Combine(root, "mail", "Trash"), "*.eml"));

		SyncItem inTrash = await WaitUntil.ResultAsync(async () =>
				(await client.PullAllAsync(trash)).Adds.FirstOrDefault(add =>
					add.ApplicationData.Element(E + "Subject")?.Value == $"Doomed {marker}"),
			$"message '{marker}' in Trash");

		await client.DeleteItemAsync(trash, inTrash.ServerId, false);
		Assert.Empty(Directory.GetFiles(Path.Combine(root, "mail", "Trash"), "*.eml"));
	}

	/// <summary>
	///   The EAS 16.x draft path, which only works because the store types its Drafts directory as
	///   <c>FolderType.Drafts</c> and refuses draft writes anywhere else. The edit must keep the
	///   SAME ServerId: the host stores the revision a rewrite returns under the item's existing
	///   key, so a store that rewrites in place has to report the revision its next enumeration will.
	/// </summary>
	[Fact]
	public async Task Draft_AddEditAndSend_KeepsItsServerId_AndTheSendComesBack()
	{
		(EasTestClient client, string root) = await StartAsync("16.1");
		string marker = $"LF{Guid.NewGuid():N}"[..10];
		string drafts = client.FolderOfType(EasFolderType.Drafts).ServerId;
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(drafts);
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(drafts);
		await client.PullAllAsync(inbox);

		SyncResult add = await client.AddItemAsync(drafts, "d1",
			new XElement(E + "To", Login),
			new XElement(E + "Subject", $"Draft {marker}"),
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "written on the phone")));
		AssertAdded(add, out string draftServerId);
		Assert.Single(Directory.GetFiles(Path.Combine(root, "mail", "Drafts"), "*.eml"));

		// A second device sees it flagged as a draft — proof the folder type reached the host.
		EasTestClient observer = await SecondDeviceAsync(client, "16.1");
		string drafts2 = observer.FolderOfType(EasFolderType.Drafts).ServerId;
		await observer.InitialSyncAsync(drafts2);
		SyncItem pulled = await WaitUntil.ResultAsync(async () =>
				(await observer.PullAllAsync(drafts2)).Adds.FirstOrDefault(item =>
					item.ApplicationData.Element(E + "Subject")?.Value == $"Draft {marker}"),
			"draft on the second device");
		Assert.Equal("1", pulled.ApplicationData.Element(E2 + "IsDraft")?.Value);

		// Edit the body: the draft is rewritten in place and keeps its ServerId.
		SyncResult edit = await client.ChangeItemAsync(drafts, draftServerId,
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "second thoughts")));
		Assert.Equal("1", edit.Status);
		Assert.Single(Directory.GetFiles(Path.Combine(root, "mail", "Drafts"), "*.eml"));

		SyncItem edited = await WaitUntil.ResultAsync(async () =>
				(await observer.PullAllAsync(drafts2)).Changes.FirstOrDefault(item =>
					item.ServerId == pulled.ServerId),
			"the edited draft as a Change, not a delete plus an add");
		Assert.Equal(pulled.ServerId, edited.ServerId);

		// email2:Send submits it; the loopback puts it in the sender's own Inbox and the draft goes.
		SyncResult send = await client.SyncAsync(drafts, new XElement(AS + "Commands",
			new XElement(AS + "Change",
				new XElement(AS + "ServerId", draftServerId),
				new XElement(E2 + "Send"),
				new XElement(AS + "ApplicationData",
					new XElement(ASB + "Body",
						new XElement(ASB + "Type", "1"),
						new XElement(ASB + "Data", "final version"))))));
		Assert.Equal("1", send.Status);

		await WaitUntil.TrueAsync(async () =>
				(await client.PullAllAsync(inbox)).Adds.Any(item =>
					item.ApplicationData.Element(E + "Subject")?.Value == $"Draft {marker}"),
			$"sent draft '{marker}' looping back into the Inbox");
		Assert.Empty(Directory.GetFiles(Path.Combine(root, "mail", "Drafts"), "*.eml"));
	}

	/// <summary>
	///   Push: a parked Ping returns as soon as a file appears in the watched directory. This is the
	///   filesystem watcher and its change latch under test — with the poll as the backstop, the
	///   answer must arrive well inside the heartbeat either way.
	/// </summary>
	[Fact]
	public async Task Ping_Returns_WhenAMessageFileIsDropped()
	{
		(EasTestClient client, string root) = await StartAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		Task<(string Status, List<string> ChangedFolders)> ping = client.PingAsync(60, inbox);
		await Task.Delay(TimeSpan.FromSeconds(2));
		await File.WriteAllTextAsync(
			Path.Combine(root, "mail", "Inbox", "pushed.eml"),
			EasTestClient.BuildMime(Login, Login, "Pushed", "dropped while a Ping was parked"));

		(string status, List<string> changed) = await ping;

		// Status 2 = "changes in these folders". No wall-clock assertion — push latency is unstable
		// under CI load, and the poll backstop is a legitimate way to get here.
		Assert.Equal("2", status);
		Assert.Contains(inbox, changed);
	}

	/// <summary>
	///   The provider implements no <see cref="ActiveSync.Contracts.ICredentialVerifier" /> — a
	///   directory holds no credentials — so logins are decided locally: the declared user's gateway
	///   password works, a wrong password does not, and an undeclared login is refused outright.
	/// </summary>
	[Fact]
	public async Task Auth_AcceptsTheDeclaredUserOnly()
	{
		(EasTestClient client, string _) = await StartAsync();
		WebApplicationFactory<Program> factory = _factories[^1];

		using HttpResponseMessage good = await client.OptionsAsync();
		Assert.Equal(HttpStatusCode.OK, good.StatusCode);

		EasTestClient wrongPassword = new(
			factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			Login, "not-the-password", "DEVWRONGPASSWORD");
		using HttpResponseMessage refused = await wrongPassword.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

		EasTestClient undeclared = new(
			factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			"nobody@example.com", Password, "DEVUNDECLARED01");
		using HttpResponseMessage stranger = await undeclared.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
	}

	// ---------- helpers ----------

	/// <summary>Boots a gateway whose every role is the plugin, and returns a handshaken device.</summary>
	private async Task<(EasTestClient Client, string Root)> StartAsync(string protocolVersion = "14.1")
	{
		string pluginsRoot = NewDirectory("as-lfs-plugins");
		string dataRoot = NewDirectory("as-lfs-data");

		Assert.True(File.Exists(StagedPluginDll),
			$"plugin not staged at {StagedPluginDll} — check the StageLocalFilesPlugin build target");
		// The loader requires the entry assembly to be named after its own directory.
		string pluginDir = Path.Combine(pluginsRoot, PluginName);
		Directory.CreateDirectory(pluginDir);
		File.Copy(StagedPluginDll, Path.Combine(pluginDir, PluginName + ".dll"), true);

		Dictionary<string, string?> overrides = new()
		{
			["ActiveSync:Plugins:Directory"] = pluginsRoot,
			// Declared-users-only: the provider is not an ICredentialVerifier, so the gateway must
			// be able to decide this login without asking a backend.
			[$"ActiveSync:Users:{Login}:Password"] = GatewayPasswordHasher.Hash(Password),
			[$"ActiveSync:Users:{Login}:MailAddress"] = Login,
			["ActiveSync:AutoProvisionUsers"] = "false"
		};
		foreach (string role in (string[])
		         ["MailStore", "MailSubmit", "Calendar", "Tasks", "Contacts", "Notes"])
		{
			overrides[$"ActiveSync:Backends:{role}:Provider"] = "local-files";
			overrides[$"ActiveSync:Backends:{role}:RootPath"] = Path.Combine(dataRoot, "{user}");
			overrides[$"ActiveSync:Backends:{role}:BasePath"] = dataRoot;
			overrides[$"ActiveSync:Backends:{role}:PollSeconds"] = "2";
		}

		// The Oof role is deliberately left on the fixture's own assignment: a section present with
		// a null Provider fails startup validation, and Oof is only contacted on Settings->Oof Set.
		WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(overrides);
		_factories.Add(factory);

		EasTestClient client = new(
			factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			Login, Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant())
		{
			ProtocolVersion = protocolVersion
		};
		await client.HandshakeAsync();
		return (client, Path.Combine(dataRoot, Login));
	}

	/// <summary>A second device of the same user against the same gateway.</summary>
	private async Task<EasTestClient> SecondDeviceAsync(EasTestClient first, string protocolVersion = "14.1")
	{
		EasTestClient second = new(
			_factories[^1].CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			first.User, Password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant())
		{
			ProtocolVersion = protocolVersion
		};
		await second.HandshakeAsync();
		return second;
	}

	private string NewDirectory(string prefix)
	{
		string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		_directories.Add(path);
		return path;
	}

	private static async Task<string> SingleFileTextAsync(string root, string collection, string pattern)
	{
		string[] files = Directory.GetFiles(Path.Combine(root, collection), pattern, SearchOption.AllDirectories);
		string file = Assert.Single(files);
		return await File.ReadAllTextAsync(file);
	}

	private static void AssertAdded(SyncResult result, out string serverId)
	{
		XElement? add = result.Responses.FirstOrDefault(response => response.Name.LocalName == "Add");
		Assert.NotNull(add);
		Assert.Equal("1", add.Element(AS + "Status")?.Value);
		serverId = add.Element(AS + "ServerId")?.Value ?? "";
		Assert.False(string.IsNullOrEmpty(serverId));
	}
}
