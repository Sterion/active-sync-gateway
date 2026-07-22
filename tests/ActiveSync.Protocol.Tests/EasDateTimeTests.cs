using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Protocol.Tests;

public class EasDateTimeTests
{
	// W15: ToLong/ToCompact must NOT shift a DateTimeKind.Unspecified value by the machine
	// offset. The parameter is named `utc` — an Unspecified value is taken at face value as UTC,
	// never treated as local-and-converted (which subtracts the host offset, corrupting the wire
	// value on any non-UTC host while looking fine in UTC CI).
	[Fact]
	public void ToLong_UnspecifiedKind_TreatedAsUtc_NoMachineOffsetShift()
	{
		DateTime unspecified = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Unspecified);
		Assert.Equal("2026-06-01T09:00:00.000Z", EasDateTime.ToLong(unspecified));
	}

	[Fact]
	public void ToCompact_UnspecifiedKind_TreatedAsUtc_NoMachineOffsetShift()
	{
		DateTime unspecified = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Unspecified);
		Assert.Equal("20260601T090000Z", EasDateTime.ToCompact(unspecified));
	}

	[Fact]
	public void ToLong_LocalKind_ConvertedToUtc()
	{
		DateTime local = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc).ToLocalTime();
		Assert.Equal("2026-06-01T09:00:00.000Z", EasDateTime.ToLong(local));
	}

	[Fact]
	public void ToCompact_UtcKind_Unchanged()
	{
		DateTime utc = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
		Assert.Equal("20260601T090000Z", EasDateTime.ToCompact(utc));
	}

	// W16: Parse must not throw an uncontrolled FormatException on phone-supplied garbage — it
	// routes through the protocol-error channel (WbxmlException → HTTP 400) instead of a 500.
	[Fact]
	public void Parse_Garbage_ThrowsWbxmlException_NotFormatException()
	{
		Assert.Throws<WbxmlException>(() => EasDateTime.Parse("not-a-date"));
	}

	// W16: the loose DateTime.Parse fallback accepted spec-violating, culture-dependent forms
	// like "3/4/2026". Only the exact MS-ASDTYPE formats are valid now.
	[Fact]
	public void Parse_LooseCultureDependentForm_Rejected()
	{
		Assert.Throws<WbxmlException>(() => EasDateTime.Parse("3/4/2026"));
	}

	// W16 coverage: the exact MS-ASDTYPE forms, including the no-Z basic form.
	[Theory]
	[InlineData("2026-06-01T09:00:00.000Z")]
	[InlineData("2026-06-01T09:00:00Z")]
	[InlineData("20260601T090000Z")]
	[InlineData("20260601T090000")] // basic form without the trailing Z
	public void TryParse_ExactForms_Accepted(string value)
	{
		Assert.True(EasDateTime.TryParse(value, out DateTime result));
		Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), result);
		Assert.Equal(DateTimeKind.Utc, result.Kind);
	}

	[Fact]
	public void TryParse_CompactWithMilliseconds_Accepted()
	{
		Assert.True(EasDateTime.TryParse("20260601T090000123Z", out DateTime result));
		Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0, 123, DateTimeKind.Utc), result);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("garbage")]
	[InlineData("3/4/2026")]
	public void TryParse_NullOrGarbage_ReturnsFalse(string? value)
	{
		Assert.False(EasDateTime.TryParse(value, out _));
	}
}
