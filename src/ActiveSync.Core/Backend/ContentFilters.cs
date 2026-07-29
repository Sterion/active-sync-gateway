using ActiveSync.Contracts;
using ActiveSync.Protocol;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Maps the client's AirSync FilterType — an EAS WIRE integer — onto the typed
///   <see cref="ContentFilter" /> a store receives. This lives HOST-side on purpose: the wire
///   numbering is protocol encoding, so a plugin must never have to know that "5" means one
///   month. A store only ever sees the resulting instant.
/// </summary>
public static class ContentFilters
{
	/// <summary>Maps an AirSync FilterType value for the Email class to a date window (1 = 1 day back … 7 = 6 months back); any other value returns <see cref="ContentFilter.All" />.</summary>
	/// <param name="filterType">The client-supplied AirSync FilterType.</param>
	/// <returns>A filter matching items no older than the mapped window, or <see cref="ContentFilter.All" /> for an unrecognized value.</returns>
	public static ContentFilter FromMailFilterType(int filterType)
	{
		return filterType switch
		{
			1 => Since(DateTimeOffset.UtcNow.AddDays(-1)),
			2 => Since(DateTimeOffset.UtcNow.AddDays(-3)),
			3 => Since(DateTimeOffset.UtcNow.AddDays(-7)),
			4 => Since(DateTimeOffset.UtcNow.AddDays(-14)),
			5 => Since(DateTimeOffset.UtcNow.AddMonths(-1)),
			6 => Since(DateTimeOffset.UtcNow.AddMonths(-3)),
			7 => Since(DateTimeOffset.UtcNow.AddMonths(-6)),
			_ => ContentFilter.All
		};
	}

	/// <summary>Maps an AirSync FilterType value for the Calendar class to a date window (4 = 2 weeks back … 7 = 6 months back); any other value (including the mail-only 1-3) returns <see cref="ContentFilter.All" />.</summary>
	/// <param name="filterType">The client-supplied AirSync FilterType.</param>
	/// <returns>A filter matching items no older than the mapped window, or <see cref="ContentFilter.All" /> for an unrecognized value.</returns>
	public static ContentFilter FromCalendarFilterType(int filterType)
	{
		return filterType switch
		{
			4 => Since(DateTimeOffset.UtcNow.AddDays(-14)),
			5 => Since(DateTimeOffset.UtcNow.AddMonths(-1)),
			6 => Since(DateTimeOffset.UtcNow.AddMonths(-3)),
			7 => Since(DateTimeOffset.UtcNow.AddMonths(-6)),
			_ => ContentFilter.All
		};
	}

	/// <summary>
	///   Picks the filter window appropriate to a store's content class: mail and calendar
	///   have their own FilterType→date-window mappings; everything else (contacts, tasks,
	///   notes) is never date-filtered.
	/// </summary>
	/// <param name="easClass">The store's EAS content class.</param>
	/// <param name="filterType">The client-supplied AirSync FilterType.</param>
	/// <returns>The filter to hand the store.</returns>
	public static ContentFilter ForClass(string easClass, int filterType)
	{
		return easClass switch
		{
			EasClass.Email => FromMailFilterType(filterType),
			EasClass.Calendar => FromCalendarFilterType(filterType),
			_ => ContentFilter.All
		};
	}

	private static ContentFilter Since(DateTimeOffset since)
	{
		return new ContentFilter { Since = since };
	}
}
