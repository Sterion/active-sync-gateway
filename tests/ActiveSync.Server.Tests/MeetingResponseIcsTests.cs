using ActiveSync.Server.Eas.Handlers;

namespace ActiveSync.Server.Tests;

/// <summary>
///   F32 — MeetingResponse must unfold RFC 5545 folded content lines before scanning for UID and
///   ORGANIZER. Exchange/Google UIDs and ORGANIZER values routinely exceed 75 octets and fold onto
///   a continuation line beginning with a space/tab; a raw split truncates the UID (event not
///   found) and can push the "mailto:" onto the continuation (reply mailed to the wrong address).
/// </summary>
public sealed class MeetingResponseIcsTests
{
	// A UID folded across two lines must be recombined into the full value.
	[Fact]
	public void ExtractUid_FoldedUid_ReturnsFullValue()
	{
		string ics =
			"BEGIN:VEVENT\r\n" +
			"UID:040000008200E00074C5B7101A82E00800000000AAAA\r\n" +
			" BBBBCCCCDDDDEEEEFFFF00001111222233334444@example.test\r\n" +
			"END:VEVENT\r\n";

		string? uid = MeetingResponseHandler.ExtractUid(ics);

		Assert.Equal(
			"040000008200E00074C5B7101A82E00800000000AAAABBBBCCCCDDDDEEEEFFFF00001111222233334444@example.test",
			uid);
	}

	// An ORGANIZER whose "mailto:" lands on the continuation line must still yield the address.
	[Fact]
	public void ExtractOrganizerEmail_FoldedOrganizer_ReturnsAddress()
	{
		string ics =
			"BEGIN:VEVENT\r\n" +
			"ORGANIZER;CN=A Very Long Display Name That Forces The Property To Fold Past 75:\r\n" +
			" mailto:organizer@example.test\r\n" +
			"END:VEVENT\r\n";

		string? email = MeetingResponseHandler.ExtractOrganizerEmail(ics, "fallback@example.test");

		Assert.Equal("organizer@example.test", email);
	}
}
