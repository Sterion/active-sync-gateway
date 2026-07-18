using ActiveSync.Backends.Sieve;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The gateway-owned sieve script: vacation shape, dot-stuffing inside the text: block,
///   the currentdate window for scheduled Oof, and the quoted-string escaping helpers.
/// </summary>
public sealed class SieveVacationScriptTests
{
	[Fact]
	public void PlainOof_UsesVacationOnly()
	{
		string script = SieveVacationScript.Build("I am away.\nBack next week.");
		Assert.Contains("require [\"vacation\"];", script, StringComparison.Ordinal);
		Assert.DoesNotContain("currentdate", script, StringComparison.Ordinal);
		Assert.Contains("vacation :days 1 text:\r\nI am away.\r\nBack next week.\r\n.\r\n;", script,
			StringComparison.Ordinal);
	}

	[Fact]
	public void ScheduledOof_WrapsVacationInDateWindow()
	{
		string script = SieveVacationScript.Build("Away.",
			new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc),
			new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc));
		Assert.Contains("require [\"vacation\", \"date\", \"relational\"];", script, StringComparison.Ordinal);
		Assert.Contains("\"ge\" \"date\" \"2026-07-20\"", script, StringComparison.Ordinal);
		Assert.Contains("\"le\" \"date\" \"2026-08-02\"", script, StringComparison.Ordinal);
		Assert.Contains(":zone \"+0000\"", script, StringComparison.Ordinal);
	}

	[Fact]
	public void MessageLines_AreDotStuffed_AndCrlfNormalized()
	{
		string script = SieveVacationScript.Build("line1\r..already\n.\nend");
		// A leading dot gains one more dot; the lone "." line cannot terminate the block.
		Assert.Contains("line1\r\n...already\r\n..\r\nend\r\n.\r\n;", script, StringComparison.Ordinal);
	}

	[Fact]
	public void ManagedMarker_IsPresent()
	{
		Assert.StartsWith("# Managed by the ActiveSync gateway", SieveVacationScript.Build("x"),
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("plain", "\"plain\"")]
	[InlineData("with \"quotes\"", "\"with \\\"quotes\\\"\"")]
	[InlineData("back\\slash", "\"back\\\\slash\"")]
	public void QuotedStrings_EscapeAndRoundTrip(string raw, string quoted)
	{
		Assert.Equal(quoted, ManageSieveClient.Quote(raw));
		Assert.Equal(raw, ManageSieveClient.Unescape(quoted[1..^1]));
	}
}
