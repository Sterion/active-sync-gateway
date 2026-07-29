using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   JMAP calendar store against a live JMAP-groupware server (Stalwart 0.16): the
///   JSCalendar ⇄ iCalendar bridge end-to-end at the store layer. The store's currency is
///   iCalendar now — the EAS half is host-side — so these drive and assert iCalendar directly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class JmapCalendarStoreTests
{
	private static JmapCalendarStore Store()
	{
		JmapClient client = new(
			new Uri(TestBackend.JmapGroupwareUrl),
			new BackendCredentials { UserName = TestBackend.JmapGroupwareUser, Password = TestBackend.JmapGroupwarePassword },
			allowInvalidCertificates: true);
		return new JmapCalendarStore(client, "admin@example.com", 5);
	}

	/// <summary>A complete VEVENT — the shape the HOST hands the store after its own merge.</summary>
	private static CalendarItem Event(string uid, string subject, params string[] extraLines)
	{
		string[] lines =
		[
			"BEGIN:VCALENDAR",
			"VERSION:2.0",
			"PRODID:-//ActiveSync Gateway//EN",
			"BEGIN:VEVENT",
			$"UID:{uid}",
			$"SUMMARY:{subject}",
			.. extraLines,
			"END:VEVENT",
			"END:VCALENDAR",
			""
		];
		return new CalendarItem { ICalendar = string.Join("\r\n", lines) };
	}

	/// <summary>The unfolded iCalendar property lines, so an assertion can name one exactly.</summary>
	private static IReadOnlyList<string> Lines(string ics) =>
		ics.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

	private static string? Line(string ics, string property) =>
		Lines(ics).FirstOrDefault(l =>
			l.StartsWith(property + ":", StringComparison.Ordinal) ||
			l.StartsWith(property + ";", StringComparison.Ordinal));

	[JmapGroupwareFact]
	public async Task Event_CreateGetUpdateDelete_RoundTrips()
	{
		JmapCalendarStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;

		string subject = $"Sprint Review {Guid.NewGuid():N}"[..20];
		string uid = Guid.NewGuid().ToString();
		CalendarItem create = Event(uid, subject,
			"DTSTART:20260720T100000Z",
			"DTEND:20260720T110000Z",
			"LOCATION:Room 1",
			"TRANSP:OPAQUE");

		(ItemKey itemKey, ItemRevision revision) = await store.CreateItemAsync(folderKey, create, CancellationToken.None);
		Assert.NotEmpty(itemKey.Value);
		Assert.NotEmpty(revision.Value);

		try
		{
			CalendarItem? item = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			Assert.NotNull(item);
			Assert.Contains("BEGIN:VEVENT", item!.ICalendar);
			Assert.Equal($"SUMMARY:{subject}", Line(item.ICalendar, "SUMMARY"));
			Assert.Equal("LOCATION:Room 1", Line(item.ICalendar, "LOCATION"));
			// The JSCalendar bridge round-trips the instant, not the literal form: a UTC "…Z" stamp
			// comes back as TZID=Etc/UTC, which is the same moment.
			Assert.Contains("20260720T100000", Line(item.ICalendar, "DTSTART"));
			Assert.Contains("20260720T110000", Line(item.ICalendar, "DTEND"));

			IReadOnlyDictionary<ItemKey, ItemRevision> revs =
				await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
			Assert.Contains(itemKey, revs.Keys);

			CalendarItem update = Event(uid, subject + " (moved)",
				"DTSTART:20260720T100000Z",
				"DTEND:20260720T110000Z",
				"LOCATION:Room 2",
				"TRANSP:OPAQUE");
			await store.UpdateItemAsync(folderKey, itemKey, update, null, CancellationToken.None);

			CalendarItem? updated = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			Assert.Equal("LOCATION:Room 2", Line(updated!.ICalendar, "LOCATION"));
			Assert.Equal($"SUMMARY:{subject} (moved)", Line(updated.ICalendar, "SUMMARY"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}

		IReadOnlyDictionary<ItemKey, ItemRevision> after =
			await store.GetItemRevisionsAsync(folderKey, ContentFilter.All, CancellationToken.None);
		Assert.DoesNotContain(itemKey, after.Keys);
	}

	/// <summary>
	///   Recurrence must survive create → get against the live server. Stalwart 0.16 speaks
	///   the JSCalendar-draft <c>recurrenceRule</c> (a single object) and rejects RFC 8984's
	///   <c>recurrenceRules</c> array outright, so before the fix this was not merely lossy: the
	///   create failed with <c>invalidProperties</c>.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Event_Recurrence_RoundTripsThroughTheServer()
	{
		JmapCalendarStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;

		string subject = $"Standup {Guid.NewGuid():N}"[..16];
		(ItemKey itemKey, ItemRevision _) = await store.CreateItemAsync(
			folderKey,
			Event(Guid.NewGuid().ToString(), subject,
				"DTSTART:20260720T090000Z",
				"DTEND:20260720T091500Z",
				"TRANSP:OPAQUE",
				"RRULE:FREQ=WEEKLY;COUNT=5;BYDAY=MO"),
			CancellationToken.None);

		try
		{
			CalendarItem? item = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			Assert.NotNull(item);
			string? rrule = Line(item!.ICalendar, "RRULE");
			Assert.NotNull(rrule);
			Assert.Contains("FREQ=WEEKLY", rrule);
			Assert.Contains("BYDAY=MO", rrule);
			Assert.Contains("COUNT=5", rrule);
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}

	/// <summary>
	///   The ordinal on a recurrence day must survive the server. "2nd Tuesday of the month"
	///   degraded to "every Tuesday" because <c>nthOfPeriod</c> was mapped in neither direction.
	///   Stalwart 0.16 does store and return it, so this is a full end-to-end reproducer.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Event_RecurrenceDayOrdinal_RoundTripsThroughTheServer()
	{
		JmapCalendarStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;

		string subject = $"Board {Guid.NewGuid():N}"[..14];
		(ItemKey itemKey, ItemRevision _) = await store.CreateItemAsync(
			folderKey,
			Event(Guid.NewGuid().ToString(), subject,
				"DTSTART:20260714T090000Z",
				"DTEND:20260714T100000Z",
				"TRANSP:OPAQUE",
				"RRULE:FREQ=MONTHLY;BYDAY=2TU"),
			CancellationToken.None);

		try
		{
			CalendarItem? item = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			string? rrule = Line(item!.ICalendar, "RRULE");
			Assert.NotNull(rrule);
			Assert.Contains("FREQ=MONTHLY", rrule);
			Assert.Contains("2TU", rrule); // the ordinal, not a bare TU
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}

	/// <summary>
	///   The calendar half of the PatchObject question. Free → busy is the case that reaches
	///   the JSCalendar layer as a *cleared* member: TRANSP:OPAQUE is expressed by omitting
	///   <c>freeBusyStatus</c> entirely, so under patch semantics the server would keep the old
	///   "free" forever unless an explicit null is sent.
	/// </summary>
	[JmapGroupwareFact]
	public async Task Update_ClearingAManagedField_ReachesTheServer()
	{
		JmapCalendarStore store = Store();
		FolderKey folderKey = (await store.ListFoldersAsync(CancellationToken.None))[0].Key;

		string subject = $"Clearing {Guid.NewGuid():N}"[..18];
		string uid = Guid.NewGuid().ToString();
		(ItemKey itemKey, ItemRevision _) = await store.CreateItemAsync(
			folderKey,
			Event(uid, subject,
				"DTSTART:20260722T100000Z",
				"DTEND:20260722T110000Z",
				"TRANSP:TRANSPARENT"),
			CancellationToken.None);

		try
		{
			CalendarItem? free = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			Assert.Equal("TRANSP:TRANSPARENT", Line(free!.ICalendar, "TRANSP"));

			await store.UpdateItemAsync(folderKey, itemKey,
				Event(uid, subject,
					"DTSTART:20260722T100000Z",
					"DTEND:20260722T110000Z",
					"TRANSP:OPAQUE"),
				null, CancellationToken.None);

			CalendarItem? busy = await store.GetItemAsync(folderKey, itemKey, CancellationToken.None);
			// OPAQUE is the iCalendar default, so the bridge may omit TRANSP entirely — what must
			// NOT survive is the stale TRANSPARENT the patch semantics would otherwise keep.
			Assert.NotEqual("TRANSP:TRANSPARENT", Line(busy!.ICalendar, "TRANSP"));
		}
		finally
		{
			await store.DeleteItemAsync(folderKey, itemKey, false, CancellationToken.None);
		}
	}
}
