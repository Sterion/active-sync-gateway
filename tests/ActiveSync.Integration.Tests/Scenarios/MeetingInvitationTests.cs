using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Outbound iMIP. Local calendar store: the gateway is the only possible scheduler, so
///   the full lifecycle is asserted (invite once, reminder edit silent, CANCEL on delete).
///   CalDAV/Stalwart: the server advertises calendar-auto-schedule and mails invitations
///   itself on PUT (verified live) — exactly ONE invitation proves the Auto probe kept the
///   gateway silent (a probe failure would make it two).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class MeetingInvitationTests(GatewayFixture gateway)
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private static XElement[] Meeting(string subject, DateTime startUtc, string attendee)
	{
		return
		[
			new XElement(Cal + "TimeZone", Convert.ToBase64String(new byte[172])),
			new XElement(Cal + "AllDayEvent", "0"),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(startUtc)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(startUtc.AddHours(1))),
			new XElement(Cal + "Subject", subject),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Sensitivity", "0"),
			new XElement(Cal + "Attendees",
				new XElement(Cal + "Attendee",
					new XElement(Cal + "Email", attendee),
					new XElement(Cal + "Name", "Attendee")))
		];
	}

	[BackendFact]
	public async Task LocalStore_MeetingLifecycle_InvitesOnce_SilentReminderEdit_CancelsOnDelete()
	{
		EasTestClient organizer = gateway.CreateLocalStoresEasClient(TestBackend.User1);
		await organizer.HandshakeAsync();
		string marker = $"MI{Guid.NewGuid():N}"[..12];
		string calendar = organizer.FolderOfType(EasFolderType.Calendar).ServerId;
		await organizer.InitialSyncAsync(calendar);
		await organizer.PullAllAsync(calendar);

		SyncResult add = await organizer.AddItemAsync(calendar, "mi1",
			Meeting(marker, DateTime.UtcNow.Date.AddDays(6).AddHours(10), TestBackend.User2));
		XElement? addResponse = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.Equal("1", addResponse?.Element(EasNamespaces.AirSync + "Status")?.Value);
		string serverId = addResponse!.Element(EasNamespaces.AirSync + "ServerId")!.Value;

		await WaitUntil.TrueAsync(
			() => ImapProbe.MessageExistsAsync(TestBackend.User2, "INBOX", $"Invitation: {marker}"),
			$"invitation for '{marker}' in the attendee's inbox");

		// Reminder-only edit: scheduling-insignificant → no mail. The delete below is the
		// ordering barrier — once its CANCEL arrived, the reminder edit has long been
		// processed, so the absence of an update mail is meaningful.
		Assert.Equal("1", (await organizer.ChangeItemAsync(calendar, serverId,
			new XElement(Cal + "Reminder", "15"))).Status);

		Assert.Equal("1", (await organizer.DeleteItemAsync(calendar, serverId, false)).Status);
		await WaitUntil.TrueAsync(
			() => ImapProbe.MessageExistsAsync(TestBackend.User2, "INBOX", $"Cancelled: {marker}"),
			$"cancellation for '{marker}' in the attendee's inbox");

		Assert.Equal(1, await ImapProbe.CountMessagesAsync(
			TestBackend.User2, "INBOX", $"Invitation: {marker}"));
		Assert.Equal(0, await ImapProbe.CountMessagesAsync(
			TestBackend.User2, "INBOX", $"Updated invitation: {marker}"));
	}

	[BackendFact]
	public async Task CalDav_AutoProbe_LeavesSchedulingToTheServer()
	{
		EasTestClient organizer = gateway.CreateEasClient(TestBackend.User1);
		await organizer.HandshakeAsync();
		string marker = $"MA{Guid.NewGuid():N}"[..12];
		string calendar = organizer.FolderOfType(EasFolderType.Calendar).ServerId;
		await organizer.InitialSyncAsync(calendar);
		await organizer.PullAllAsync(calendar);

		SyncResult add = await organizer.AddItemAsync(calendar, "ma1",
			Meeting(marker, DateTime.UtcNow.Date.AddDays(7).AddHours(9), TestBackend.User2));
		XElement? addResponse = add.Responses.FirstOrDefault(r => r.Name.LocalName == "Add");
		Assert.Equal("1", addResponse?.Element(EasNamespaces.AirSync + "Status")?.Value);
		string serverId = addResponse!.Element(EasNamespaces.AirSync + "ServerId")!.Value;
		try
		{
			// Stalwart schedules implicitly: ITS invitation arrives. Had the gateway also
			// mailed (Auto probe failure), the attendee would see two.
			await WaitUntil.TrueAsync(
				async () => await ImapProbe.CountMessagesAsync(TestBackend.User2, "INBOX", marker) > 0,
				$"the server's own invitation for '{marker}'");
			await Task.Delay(TimeSpan.FromSeconds(3)); // give a hypothetical duplicate time to land
			Assert.Equal(1, await ImapProbe.CountMessagesAsync(TestBackend.User2, "INBOX", marker));
		}
		finally
		{
			await organizer.DeleteItemAsync(calendar, serverId, false);
		}
	}
}
