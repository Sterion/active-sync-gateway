using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Data.Sqlite;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Shared-calendar grants (`eas share`): a database grant surfaces in the user's folder
///   list handling, a read-only grant silently reverts client writes (ReadOnly-mode
///   semantics per folder), and an unreachable granted href is skipped without breaking
///   folder sync. Cross-user ACLs are a server concern — the same-user second collection
///   proves the whole gateway mechanism.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class SharedCalendarTests(GatewayFixture gateway)
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	[SkipOnStackFact("cyrus,axigen", "Shared calendars need a DAV backend with per-user-addressable collection paths: Cyrus's shared-collection enforcement differs from the gateway's revert model, and Axigen serves calendars from a fixed mailbox-relative /Calendar/ root with no cross-principal href.")]
	public async Task ReadOnlyGrant_RevertsClientWrites_AndBadHrefIsSkipped()
	{
		if (TestBackend.DavUrl is null)
			return;
		string? collectionHref = await EnsureSecondCalendarAsync(TestBackend.User2, "GatewayShared");
		if (collectionHref is null)
			return; // server without MKCALENDAR — mechanism not testable on this stack

		string dbPath = Path.Combine(Path.GetTempPath(), $"activesync-share-{Guid.NewGuid():N}.db");
		try
		{
			await using Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory =
				gateway.CreateIsolatedFactory(new Dictionary<string, string?>
				{
					["ActiveSync:Database:ConnectionString"] = $"Data Source={dbPath}"
				});
			// Starting the host runs the migrations; the grants must exist BEFORE the first
			// authenticated request builds the backend session.
			using HttpClient http = factory.CreateClient(
				new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
				{
					AllowAutoRedirect = false
				});
			await InsertGrantAsync(dbPath, TestBackend.User2, collectionHref, true);
			await InsertGrantAsync(dbPath, TestBackend.User2, "/no/such/collection/", false);

			EasTestClient device = new(http, TestBackend.User2, TestBackend.Password,
				$"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
			await device.HandshakeAsync(); // the unreachable grant must not break FolderSync
			EasFolder shared = Assert.Single(device.Folders, f =>
				f.Type == EasFolderType.UserCalendar && f.DisplayName == "GatewayShared");

			// A write into the read-only folder is silently reverted: the Add is rejected
			// per-item and nothing lands on the DAV server.
			await device.InitialSyncAsync(shared.ServerId);
			await device.PullAllAsync(shared.ServerId);
			DateTime start = DateTime.UtcNow.Date.AddDays(4).AddHours(9);
			SyncResult add = await device.AddItemAsync(shared.ServerId, "sc1",
				new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
				new XElement(Cal + "AllDayEvent", "0"),
				new XElement(Cal + "StartTime", EasDateTime.ToCompact(start)),
				new XElement(Cal + "EndTime", EasDateTime.ToCompact(start.AddHours(1))),
				new XElement(Cal + "Subject", "Should not exist"),
				new XElement(Cal + "BusyStatus", "2"),
				new XElement(Cal + "Sensitivity", "0"));
			XElement? addResponse = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
			Assert.NotNull(addResponse);
			Assert.Equal("6", addResponse.Element(AS + "Status")?.Value);

			SyncResult after = await device.PullAllAsync(shared.ServerId);
			Assert.DoesNotContain(after.Adds, a =>
				a.ApplicationData.Element(Cal + "Subject")?.Value == "Should not exist");
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

	private static async Task InsertGrantAsync(
		string dbPath, string userName, string collectionHref, bool readOnly)
	{
		// Exactly what `eas share add` writes.
		await using SqliteConnection connection = new($"Data Source={dbPath}");
		await connection.OpenAsync();
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			"INSERT INTO SharedCalendarGrants (UserName, CollectionHref, ReadOnly, CreatedUtc) " +
			"VALUES ($user, $href, $ro, $created)";
		command.Parameters.AddWithValue("$user", userName);
		command.Parameters.AddWithValue("$href", collectionHref);
		command.Parameters.AddWithValue("$ro", readOnly ? 1 : 0);
		command.Parameters.AddWithValue("$created", DateTime.UtcNow);
		Assert.Equal(1, await command.ExecuteNonQueryAsync());
	}

	/// <summary>Creates a second VEVENT collection in the user's home set; returns its href.</summary>
	private static async Task<string?> EnsureSecondCalendarAsync(string user, string name)
	{
		if (TestBackend.DavUrl is not { } davUrl)
			return null;
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
			return null;
		}

		string target = home.TrimEnd('/') + $"/{name}/";
		using HttpClient http = new() { BaseAddress = new Uri(davUrl) };
		http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
			Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{TestBackend.Password}")));
		// Set the display name in the request: servers that invent none from the URL segment
		// (Radicale) would otherwise surface the shared folder as "<user>/<name>", not "<name>".
		XNamespace d = "DAV:";
		XNamespace c = "urn:ietf:params:xml:ns:caldav";
		XDocument mkbody = new(new XElement(c + "mkcalendar",
			new XElement(d + "set", new XElement(d + "prop", new XElement(d + "displayname", name)))));
		using HttpRequestMessage mkcalendar = new(new HttpMethod("MKCALENDAR"), target)
		{
			Content = new StringContent(mkbody.ToString(), Encoding.UTF8, "application/xml")
		};
		try
		{
			using HttpResponseMessage response = await http.SendAsync(mkcalendar);
			// 2xx = created; 405 usually = already exists. Verify by PROPFIND either way.
		}
		catch (HttpRequestException)
		{
			return null;
		}

		try
		{
			await dav.PropfindAsync(target, 0, new XElement(DavNs.D + "propfind",
				new XElement(DavNs.D + "prop", new XElement(DavNs.D + "resourcetype"))), CancellationToken.None);
			return target;
		}
		catch (BackendException)
		{
			return null;
		}
	}
}
