using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

public class FileReferenceTests
{
	[Fact]
	public void RoundTrip_Works()
	{
		string reference = ImapMailBackend.MakeFileReference("imap:INBOX", "42", 3);
		(string folder, string item, int index) = ImapMailBackend.ParseFileReference(reference);
		Assert.Equal("imap:INBOX", folder);
		Assert.Equal("42", item);
		Assert.Equal(3, index);
	}

	[Theory]
	[InlineData("not-a-reference")]
	[InlineData("a|b")] // too few parts
	[InlineData("a|b|c|d")] // too many parts
	[InlineData("a|b|notanumber")] // non-numeric index
	[InlineData("a|b|-1")] // negative index
	[InlineData("a|b|99999999999999")] // overflowing index
	public void MalformedReference_IsABackendError(string reference)
	{
		Assert.Throws<BackendException>(() => ImapMailBackend.ParseFileReference(reference));
	}
}
