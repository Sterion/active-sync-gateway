using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   ResolveRecipients Availability → MergedFreeBusy: the requesting user's own free/busy
///   is served from their calendar (local store and CalDAV free-busy-query alike), other
///   recipients degrade to per-recipient status 163 when the backend refuses access.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class FreeBusyTests(GatewayFixture gateway)
{
	private static readonly XNamespace RR = EasNamespaces.ResolveRecipients;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	[BackendFact]
	public async Task OwnAvailability_LocalStore_ShowsBusyDigits_OthersGet163()
	{
		EasTestClient client = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await client.HandshakeAsync();
		string calendar = client.FolderOfType(EasFolderType.Calendar).ServerId;
		await client.InitialSyncAsync(calendar);
		await client.PullAllAsync(calendar);

		// Busy 10:00–11:00 UTC on a fixed future day.
		DateTime day = DateTime.UtcNow.Date.AddDays(10);
		SyncResult add = await client.AddItemAsync(calendar, "fb1",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(day.AddHours(10))),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(day.AddHours(11))),
			new XElement(Cal + "Subject", "Busy hour"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"));
		Assert.Equal("1", add.Status);

		string merged = await QueryOwnAvailabilityAsync(client, TestBackend.User1, day);
		Assert.Equal(48, merged.Length); // 24 h / 30 min
		Assert.Equal('2', merged[20]); // 10:00–10:30
		Assert.Equal('2', merged[21]); // 10:30–11:00
		Assert.Equal('0', merged[0]);

		// A different recipient has no data in the local store → per-recipient 163.
		XDocument? cross = await ResolveWithAvailabilityAsync(client, TestBackend.User2, day);
		XElement? crossAvailability = cross?.Descendants(RR + "Availability").FirstOrDefault();
		Assert.Equal("163", crossAvailability?.Element(RR + "Status")?.Value);
	}

	[CalDavFreeBusyFact]
	public async Task OwnAvailability_CalDav_UsesFreeBusyQuery()
	{
		// The shared factory syncs calendars against the real CalDAV backend, so this
		// exercises the free-busy-query REPORT path end-to-end.
		EasTestClient client = gateway.CreateEasClient(TestBackend.User2);
		await client.HandshakeAsync();
		string calendar = client.FolderOfType(EasFolderType.Calendar).ServerId;
		await client.InitialSyncAsync(calendar);
		await client.PullAllAsync(calendar);

		DateTime day = DateTime.UtcNow.Date.AddDays(11);
		SyncResult add = await client.AddItemAsync(calendar, "fb2",
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(day.AddHours(14))),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(day.AddHours(15).AddMinutes(30))),
			new XElement(Cal + "Subject", "Dav busy block"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"));
		Assert.Equal("1", add.Status);

		string merged = await QueryOwnAvailabilityAsync(client, TestBackend.User2, day);
		Assert.Equal(48, merged.Length);
		Assert.Equal('2', merged[28]); // 14:00–14:30
		Assert.Equal('2', merged[29]);
		Assert.Equal('2', merged[30]); // 15:00–15:30
	}

	private static async Task<string> QueryOwnAvailabilityAsync(EasTestClient client, string user, DateTime day)
	{
		XDocument? response = await ResolveWithAvailabilityAsync(client, user, day);
		XElement? recipient = response?.Descendants(RR + "Recipient")
			.FirstOrDefault(r => string.Equals(
				r.Element(RR + "EmailAddress")?.Value, user, StringComparison.OrdinalIgnoreCase));
		Assert.NotNull(recipient);
		XElement? availability = recipient.Element(RR + "Availability");
		Assert.NotNull(availability);
		Assert.Equal("1", availability.Element(RR + "Status")?.Value);
		string? merged = availability.Element(RR + "MergedFreeBusy")?.Value;
		Assert.NotNull(merged);
		return merged;
	}

	private static Task<XDocument?> ResolveWithAvailabilityAsync(EasTestClient client, string to, DateTime day)
	{
		return client.PostAsync("ResolveRecipients", new XDocument(
			new XElement(RR + "ResolveRecipients",
				new XElement(RR + "To", to),
				new XElement(RR + "Options",
					new XElement(RR + "Availability",
						new XElement(RR + "StartTime", day.ToString("yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'",
							System.Globalization.CultureInfo.InvariantCulture)),
						new XElement(RR + "EndTime", day.AddDays(1).ToString("yyyy-MM-dd'T'HH':'mm':'ss'.'fff'Z'",
							System.Globalization.CultureInfo.InvariantCulture)))))));
	}
}
