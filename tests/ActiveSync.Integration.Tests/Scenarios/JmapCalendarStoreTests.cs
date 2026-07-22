using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   JMAP calendar store against a live JMAP-groupware server (Stalwart 0.16): the
///   JSCalendar ⇄ iCalendar ⇄ EAS bridge end-to-end at the store layer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JmapCalendarStoreTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private static JmapCalendarStore Store()
	{
		JmapClient client = new(
			new Uri(TestBackend.JmapGroupwareUrl),
			new BackendCredentials(TestBackend.JmapGroupwareUser, TestBackend.JmapGroupwarePassword),
			allowInvalidCertificates: true);
		return new JmapCalendarStore(client, "admin@example.com", 5);
	}

	[JmapGroupwareFact]
	public async Task Event_CreateGetUpdateDelete_RoundTrips()
	{
		JmapCalendarStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string subject = $"Sprint Review {Guid.NewGuid():N}"[..20];
		XElement create = new("ApplicationData",
			new XElement(Cal + "Subject", subject),
			new XElement(Cal + "StartTime", "20260720T100000Z"),
			new XElement(Cal + "EndTime", "20260720T110000Z"),
			new XElement(Cal + "Location", "Room 1"),
			new XElement(Cal + "BusyStatus", "2"));

		(string itemKey, string revision) = await store.CreateItemAsync(folderKey, create, CancellationToken.None);
		Assert.NotEmpty(itemKey);
		Assert.NotEmpty(revision);

		try
		{
			BackendItem? item = await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.NotNull(item);
			string? V(BackendItem i, string local) => i.ApplicationData.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
			Assert.Equal(subject, V(item, "Subject"));
			Assert.Equal("Room 1", V(item, "Location"));
			Assert.Equal("20260720T100000Z", V(item, "StartTime"));
			Assert.Equal("20260720T110000Z", V(item, "EndTime"));

			IReadOnlyDictionary<string, string> revs =
				await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
			Assert.Contains(itemKey, revs.Keys);

			// GetRawEvent returns iCalendar (the invitation service reads it).
			string? ics = await store.GetRawEventAsync(folderKey, itemKey, CancellationToken.None);
			Assert.NotNull(ics);
			Assert.Contains("BEGIN:VEVENT", ics);

			XElement update = new("ApplicationData",
				new XElement(Cal + "Subject", subject + " (moved)"),
				new XElement(Cal + "StartTime", "20260720T100000Z"),
				new XElement(Cal + "EndTime", "20260720T110000Z"),
				new XElement(Cal + "Location", "Room 2"),
				new XElement(Cal + "BusyStatus", "2"));
			await store.UpdateItemAsync(folderKey, itemKey, update, CancellationToken.None);

			BackendItem? updated = await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.Equal("Room 2", V(updated!, "Location"));
			Assert.Equal(subject + " (moved)", V(updated!, "Subject"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}

		IReadOnlyDictionary<string, string> after =
			await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
		Assert.DoesNotContain(itemKey, after.Keys);
	}

	/// <summary>
	///   H4 — recurrence must survive create → get against the live server. Stalwart 0.16 speaks
	///   the JSCalendar-draft <c>recurrenceRule</c> (a single object) and rejects RFC 8984's
	///   <c>recurrenceRules</c> array outright, so before the fix this was not merely lossy: the
	///   create failed with <c>invalidProperties</c>.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Event_Recurrence_RoundTripsThroughTheServer()
	{
		JmapCalendarStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string subject = $"Standup {Guid.NewGuid():N}"[..16];
		(string itemKey, _) = await store.CreateItemAsync(folderKey, new XElement("ApplicationData",
			new XElement(Cal + "Subject", subject),
			new XElement(Cal + "StartTime", "20260720T090000Z"),
			new XElement(Cal + "EndTime", "20260720T091500Z"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "1"),
				new XElement(Cal + "Interval", "1"),
				new XElement(Cal + "DayOfWeek", "2"),
				new XElement(Cal + "Occurrences", "5"))), CancellationToken.None);

		try
		{
			BackendItem? item =
				await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.NotNull(item);
			XElement? recurrence = item.ApplicationData.FirstOrDefault(e => e.Name.LocalName == "Recurrence");
			Assert.NotNull(recurrence);
			string? R(string local) => recurrence.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
			Assert.Equal("1", R("Type"));          // weekly
			Assert.Equal("2", R("DayOfWeek"));     // Monday
			Assert.Equal("5", R("Occurrences"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}

	/// <summary>
	///   H5 — the ordinal on a recurrence day must survive the server. "2nd Tuesday of the month"
	///   degraded to "every Tuesday" because <c>nthOfPeriod</c> was mapped in neither direction.
	///   Stalwart 0.16 does store and return it, so this is a full end-to-end reproducer.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Event_RecurrenceDayOrdinal_RoundTripsThroughTheServer()
	{
		JmapCalendarStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string subject = $"Board {Guid.NewGuid():N}"[..14];
		(string itemKey, _) = await store.CreateItemAsync(folderKey, new XElement("ApplicationData",
			new XElement(Cal + "Subject", subject),
			new XElement(Cal + "StartTime", "20260714T090000Z"),
			new XElement(Cal + "EndTime", "20260714T100000Z"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "3"),          // monthly, nth weekday
				new XElement(Cal + "Interval", "1"),
				new XElement(Cal + "WeekOfMonth", "2"),   // second
				new XElement(Cal + "DayOfWeek", "4"))), CancellationToken.None); // Tuesday

		try
		{
			BackendItem? item =
				await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			XElement recurrence = item!.ApplicationData.First(e => e.Name.LocalName == "Recurrence");
			string? R(string local) => recurrence.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
			Assert.Equal("3", R("Type"));
			Assert.Equal("2", R("WeekOfMonth"));
			Assert.Equal("4", R("DayOfWeek"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}

	/// <summary>
	///   H7 — the calendar half of the PatchObject question. Free → busy is the case that reaches
	///   the JSCalendar layer as a *cleared* member: BusyStatus 2 makes the iCalendar TRANSP
	///   OPAQUE, which the bridge expresses by omitting <c>freeBusyStatus</c> entirely. Under patch
	///   semantics the server then keeps the old "free" forever unless an explicit null is sent.
	///   (Most other fields cannot show this at the store layer: <c>CalendarConverter</c> merges
	///   the payload onto the stored iCalendar, so an absent Location is restored before the
	///   JSCalendar bridge ever sees it.)
	/// </summary>
	[JmapGroupwareFact]
	public async Task Update_ClearingAManagedField_ReachesTheServer()
	{
		JmapCalendarStore store = Store();
		string folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].BackendKey;

		string subject = $"Clearing {Guid.NewGuid():N}"[..18];
		(string itemKey, _) = await store.CreateItemAsync(folderKey, new XElement("ApplicationData",
			new XElement(Cal + "Subject", subject),
			new XElement(Cal + "StartTime", "20260722T100000Z"),
			new XElement(Cal + "EndTime", "20260722T110000Z"),
			new XElement(Cal + "BusyStatus", "0")), CancellationToken.None);

		try
		{
			BackendItem? free =
				await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.Equal("0", free!.ApplicationData.First(e => e.Name.LocalName == "BusyStatus").Value);

			await store.UpdateItemAsync(folderKey, itemKey, new XElement("ApplicationData",
				new XElement(Cal + "Subject", subject),
				new XElement(Cal + "StartTime", "20260722T100000Z"),
				new XElement(Cal + "EndTime", "20260722T110000Z"),
				new XElement(Cal + "BusyStatus", "2")), CancellationToken.None);

			BackendItem? busy =
				await store.GetItemAsync(folderKey, itemKey, BodyPreference.PlainText, CancellationToken.None);
			Assert.Equal("2", busy!.ApplicationData.First(e => e.Name.LocalName == "BusyStatus").Value);
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}
}
