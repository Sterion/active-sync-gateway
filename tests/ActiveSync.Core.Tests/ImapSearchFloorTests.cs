using ActiveSync.Backends.Imap;

namespace ActiveSync.Core.Tests;

/// <summary>
///   <see cref="ImapMailBackend.SearchFloor" /> backs the IMAP SINCE search floor off by one
///   extra day so a UTC filter boundary is never later than the server's own (possibly
///   non-UTC) notion of "today" — RFC 3501's SINCE comparison disregards timezone entirely, so
///   a message near a day boundary could otherwise be silently excluded from the revision map on
///   its first appearance. COVERAGE, not a red-first reproduction of the live symptom: the skew
///   only manifests against a real IMAP server whose INTERNALDATE runs in a non-UTC zone, which
///   this test environment cannot control deterministically (our own test backends store
///   INTERNALDATE in UTC). This proves the widening arithmetic itself is correct.
/// </summary>
public class ImapSearchFloorTests
{
	[Fact]
	public void BacksOffOneFullDay_AndTruncatesTime()
	{
		DateTime since = new(2026, 4, 1, 23, 50, 0, DateTimeKind.Utc);
		Assert.Equal(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc), ImapMailBackend.SearchFloor(since));
	}

	[Fact]
	public void AtExactMidnight_StillBacksOffOneDay()
	{
		// Even a `since` that already sits on a day boundary gets the same one-day margin —
		// the point is a guaranteed superset of the caller's window, not a boundary-only fix.
		DateTime since = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
		Assert.Equal(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc), ImapMailBackend.SearchFloor(since));
	}
}
