using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H30: BuildEventFilter must render the time-range start through EasDateTime.ToCompact so a
///   non-UTC SinceUtc is converted to UTC — not stamped with a trailing Z while still carrying
///   local wall-clock (which the hand-rolled ToString("...'Z'") did).
/// </summary>
public class CalDavEventFilterTests
{
	[Fact]
	public void BuildEventFilter_LocalKindSince_ConvertedToUtc()
	{
		// 09:00 local (Europe/Copenhagen, CEST +02:00 on this date) must serialize as 07:00Z.
		DateTime localSince = new DateTime(2026, 6, 1, 7, 0, 0, DateTimeKind.Utc).ToLocalTime();
		Assert.Equal(DateTimeKind.Local, localSince.Kind);

		XElement filter = CalDavStore.BuildEventFilter(new ContentFilter(localSince));
		string start = filter.Descendants()
			.First(e => e.Name.LocalName == "time-range").Attribute("start")!.Value;

		Assert.Equal("20260601T070000Z", start);
	}

	[Fact]
	public void BuildEventFilter_NullSince_UsesEpoch()
	{
		XElement filter = CalDavStore.BuildEventFilter(new ContentFilter(null));
		string start = filter.Descendants()
			.First(e => e.Name.LocalName == "time-range").Attribute("start")!.Value;

		Assert.Equal("19700101T000000Z", start);
	}
}
