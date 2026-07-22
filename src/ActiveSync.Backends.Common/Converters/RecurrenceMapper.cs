using System.Xml.Linq;
using ActiveSync.Protocol;
using Ical.Net;
using Ical.Net.DataTypes;

namespace ActiveSync.Backends.Converters;

/// <summary>
///   EAS Recurrence element ↔ iCalendar RRULE, shared by the Calendar (MS-ASCAL) and
///   Tasks (MS-ASTASK) converters — both schemas carry the same children in their own
///   namespaces. Date formatting differs per class (matching each converter's other date
///   fields): Calendar sends compact Until values, Tasks the long form.
/// </summary>
internal static class RecurrenceMapper
{
	public static XElement? Build(
		XNamespace ns, RecurrencePattern pattern, DateTime startUtc, bool longDates = false)
	{
		XElement recurrence = new(ns + "Recurrence");
		int? type = null;

		switch (pattern.Frequency)
		{
			case FrequencyType.Daily:
				type = 0;
				break;
			case FrequencyType.Weekly:
				type = 1;
				recurrence.Add(new XElement(ns + "DayOfWeek",
					(pattern.ByDay is { Count: > 0 }
						? DayOfWeekMask(pattern.ByDay.Select(d => d.DayOfWeek))
						: DayOfWeekMask([startUtc.DayOfWeek])).ToString()));
				break;
			case FrequencyType.Monthly when pattern.ByDay is { Count: > 0 }:
				type = 3;
				recurrence.Add(new XElement(ns + "WeekOfMonth", WeekOfMonth(pattern).ToString()));
				recurrence.Add(new XElement(ns + "DayOfWeek",
					DayOfWeekMask(pattern.ByDay.Select(d => d.DayOfWeek)).ToString()));
				break;
			case FrequencyType.Monthly:
				type = 2;
				recurrence.Add(new XElement(ns + "DayOfMonth",
					(pattern.ByMonthDay?.FirstOrDefault() is int dom and not 0 ? dom : startUtc.Day).ToString()));
				break;
			case FrequencyType.Yearly when pattern.ByDay is { Count: > 0 }:
				type = 6;
				recurrence.Add(new XElement(ns + "WeekOfMonth", WeekOfMonth(pattern).ToString()));
				recurrence.Add(new XElement(ns + "DayOfWeek",
					DayOfWeekMask(pattern.ByDay.Select(d => d.DayOfWeek)).ToString()));
				recurrence.Add(new XElement(ns + "MonthOfYear",
					(pattern.ByMonth?.FirstOrDefault() is int m and not 0 ? m : startUtc.Month).ToString()));
				break;
			case FrequencyType.Yearly:
				type = 5;
				recurrence.Add(new XElement(ns + "DayOfMonth",
					(pattern.ByMonthDay?.FirstOrDefault() is int ymd and not 0 ? ymd : startUtc.Day).ToString()));
				recurrence.Add(new XElement(ns + "MonthOfYear",
					(pattern.ByMonth?.FirstOrDefault() is int ym and not 0 ? ym : startUtc.Month).ToString()));
				break;
			default:
				return null; // secondly/minutely/hourly are not expressible in EAS
		}

		recurrence.AddFirst(new XElement(ns + "Type", type.ToString()));
		if (pattern.Interval > 1)
			recurrence.Add(new XElement(ns + "Interval", pattern.Interval.ToString()));
		if (pattern.Count is > 0)
			recurrence.Add(new XElement(ns + "Occurrences", pattern.Count.ToString()));
		if (pattern.Until is not null)
		{
			DateTime? until = pattern.Until.AsUtc;
			if (until is not null)
				recurrence.Add(new XElement(ns + "Until",
					longDates ? EasDateTime.ToLong(until.Value) : EasDateTime.ToCompact(until.Value)));
		}

		return recurrence;
	}

	public static RecurrencePattern? Parse(XNamespace ns, XElement recurrence)
	{
		string? V(string localName)
		{
			return recurrence.Element(ns + localName)?.Value;
		}

		int type = int.TryParse(V("Type"), out int t) ? t : -1;

		RecurrencePattern pattern = new();
		switch (type)
		{
			case 0:
				pattern.Frequency = FrequencyType.Daily;
				break;
			case 1:
				pattern.Frequency = FrequencyType.Weekly;
				if (int.TryParse(V("DayOfWeek"), out int weeklyMask))
					pattern.ByDay = MaskToDays(weeklyMask).Select(d => new WeekDay(d)).ToList();
				break;
			case 2:
				pattern.Frequency = FrequencyType.Monthly;
				if (int.TryParse(V("DayOfMonth"), out int dom))
					pattern.ByMonthDay = [dom];
				break;
			case 3:
				pattern.Frequency = FrequencyType.Monthly;
				ApplyNthDay(pattern);
				break;
			case 5:
				pattern.Frequency = FrequencyType.Yearly;
				if (int.TryParse(V("DayOfMonth"), out int ydom))
					pattern.ByMonthDay = [ydom];
				if (int.TryParse(V("MonthOfYear"), out int ymonth))
					pattern.ByMonth = [ymonth];
				break;
			case 6:
				pattern.Frequency = FrequencyType.Yearly;
				ApplyNthDay(pattern);
				if (int.TryParse(V("MonthOfYear"), out int nmonth))
					pattern.ByMonth = [nmonth];
				break;
			default:
				return null;
		}

		if (int.TryParse(V("Interval"), out int interval) && interval > 1)
			pattern.Interval = interval;
		if (int.TryParse(V("Occurrences"), out int occurrences) && occurrences > 0)
			pattern.Count = occurrences;
		else if (V("Until") is { } until && EasDateTime.TryParse(until, out DateTime untilUtc))
			pattern.Until = new CalDateTime(untilUtc, "UTC");
		return pattern;

		void ApplyNthDay(RecurrencePattern p)
		{
			int week = int.TryParse(V("WeekOfMonth"), out int w) ? w : 1;
			int offset = week == 5 ? -1 : week;
			if (int.TryParse(V("DayOfWeek"), out int mask))
				p.ByDay = MaskToDays(mask).Select(d => new WeekDay(d, offset)).ToList();
		}
	}

	private static int WeekOfMonth(RecurrencePattern pattern)
	{
		// EAS encodes "which week" as 1-5, where 5 specifically means "last week of the
		// month". iCal expresses the same via a negative offset: -1 = last. Map that -1 to
		// EAS's 5; clamp any other value into 1-5 (0/absent → first week).
		int offset = pattern.ByDay?.FirstOrDefault()?.Offset
		             ?? pattern.BySetPosition?.FirstOrDefault()
		             ?? 1;
		return offset == -1 ? 5 : Math.Clamp(offset == 0 ? 1 : offset, 1, 5);
	}

	private static int DayOfWeekMask(IEnumerable<DayOfWeek> days)
	{
		int mask = 0;
		foreach (DayOfWeek day in days)
			mask |= 1 << (int)day; // Sunday=1, Monday=2, ... Saturday=64
		return mask == 0 ? 1 : mask;
	}

	private static IEnumerable<DayOfWeek> MaskToDays(int mask)
	{
		for (int i = 0; i < 7; i++)
			if ((mask & (1 << i)) != 0)
				yield return (DayOfWeek)i;
	}
}
