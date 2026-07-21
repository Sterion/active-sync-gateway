using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

public class DelimitedKeyTests
{
	[Fact]
	public void RoundTrips_PlainParts()
	{
		string encoded = DelimitedKey.Encode("imap:INBOX", "42", "0");
		string[]? parts = DelimitedKey.Decode(encoded, 3);
		Assert.NotNull(parts);
		Assert.Equal(["imap:INBOX", "42", "0"], parts);
	}

	[Fact]
	public void PipeInsideAComponent_IsNotMistakenForTheDelimiter()
	{
		// "Work|Home" is a legal IMAP mailbox name — it must survive the round trip whole.
		string encoded = DelimitedKey.Encode("imap:Work|Home", "42", "0");
		Assert.Equal(3, encoded.Split('|').Length); // exactly the three delimiters we added
		string[]? parts = DelimitedKey.Decode(encoded, 3);
		Assert.NotNull(parts);
		Assert.Equal("imap:Work|Home", parts[0]);
		Assert.Equal("42", parts[1]);
		Assert.Equal("0", parts[2]);
	}

	[Fact]
	public void WrongPartCount_ReturnsNull()
	{
		string encoded = DelimitedKey.Encode("a", "b");
		Assert.Null(DelimitedKey.Decode(encoded, 3));
		Assert.NotNull(DelimitedKey.Decode(encoded, 2));
	}
}
