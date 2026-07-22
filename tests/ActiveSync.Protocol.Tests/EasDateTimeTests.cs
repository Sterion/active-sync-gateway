using ActiveSync.Protocol;

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
}
