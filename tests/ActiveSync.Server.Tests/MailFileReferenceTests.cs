using ActiveSync.Server.Eas.Content;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The wire-facing mail-attachment FileReference codec. It moved HOST-side with the typed item
///   currency (a store never sees a FileReference), so these are the same round-trip and
///   malformed-input cases the store-side codec used to carry — with the one deliberate change
///   that a malformed reference now answers <c>null</c> ("not found") instead of throwing.
/// </summary>
public class MailFileReferenceTests
{
	[Fact]
	public void RoundTrip_Works()
	{
		string reference = MailFileReference.Encode("imap:INBOX", "42", 3);
		(string Folder, string Item, int Index)? parsed = MailFileReference.TryParse(reference);

		Assert.NotNull(parsed);
		Assert.Equal("imap:INBOX", parsed!.Value.Folder);
		Assert.Equal("42", parsed.Value.Item);
		Assert.Equal(3, parsed.Value.Index);
	}

	[Fact]
	public void RoundTrip_SurvivesADelimiterInsideTheFolderKey()
	{
		// Per-component escaping is why the reference can carry a folder name containing '|'.
		string reference = MailFileReference.Encode("imap:Odd|Name", "7", 0);

		(string Folder, string Item, int Index)? parsed = MailFileReference.TryParse(reference);

		Assert.NotNull(parsed);
		Assert.Equal("imap:Odd|Name", parsed!.Value.Folder);
		Assert.Equal("7", parsed.Value.Item);
	}

	[Theory]
	[InlineData("not-a-reference")]
	[InlineData("a|b")] // too few parts
	[InlineData("a|b|c|d")] // too many parts
	[InlineData("a|b|notanumber")] // non-numeric index
	[InlineData("a|b|-1")] // negative index
	[InlineData("a|b|99999999999999")] // overflowing index
	public void MalformedReference_ParsesAsNull(string reference)
	{
		Assert.Null(MailFileReference.TryParse(reference));
	}
}
