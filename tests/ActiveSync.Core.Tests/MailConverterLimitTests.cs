using System.Reflection;
using System.Xml.Linq;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D25 — `Limit` (`value.Length <= max ? value : value[..max]`) can cut a UTF-16 surrogate
///   pair in half, e.g. in the To/From/Cc/ReplyTo header strings it truncates. A lone surrogate
///   in an XElement's text makes the response unserializable -- XmlWriter rejects it -- sinking
///   the whole Sync response over one oversized header rather than one message.
///   `MailConverter.Limit` is private; invoked via reflection to prove the defect at the exact
///   site the finding names without depending on MimeKit's address-list formatting.
/// </summary>
public class MailConverterLimitTests
{
	private static string InvokeLimit(string value, int max)
	{
		MethodInfo method = typeof(ActiveSync.Backends.Common.Converters.MailConverter)
			.GetMethod("Limit", BindingFlags.NonPublic | BindingFlags.Static)!;
		return (string)method.Invoke(null, [value, max])!;
	}

	[Fact]
	public void Limit_DoesNotSplitASurrogatePairAtTheCutBoundary()
	{
		// The cut lands exactly between the high and low surrogate of an emoji: 1022 ASCII
		// chars + the pair = length 1024; cutting at 1023 keeps the lone high surrogate.
		string padding = new string('a', 1022);
		string value = padding + "😀"; // U+1F600 GRINNING FACE, as a surrogate pair
		Assert.Equal(1024, value.Length);

		string limited = InvokeLimit(value, 1023);

		// A lone surrogate is not valid XML text -- constructing/serializing an XElement with
		// it must never throw, or the whole Sync response sinks over one oversized header.
		Exception? thrown = Record.Exception(() => new XElement("To", limited).ToString());
		Assert.Null(thrown);
	}
}
