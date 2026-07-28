using ActiveSync.Server.Eas;

namespace ActiveSync.Server.Tests;

/// <summary>
///   EasEndpoint.IsValidDeviceId: an empty DeviceId used to be accepted — the
///   comment claimed some tools (OPTIONS probes) omit it, but OPTIONS is mapped separately
///   (EasEndpoint.HandleOptions) and never reaches this check, so the only real effect was every
///   POST that omitted DeviceId sharing a single "" keyed Device row for that user.
/// </summary>
public sealed class EasEndpointDeviceIdTests
{
	[Fact]
	public void EmptyDeviceId_IsRejected()
	{
		Assert.False(EasEndpoint.IsValidDeviceId(""));
	}

	[Fact]
	public void NormalDeviceId_IsAccepted()
	{
		Assert.True(EasEndpoint.IsValidDeviceId("Appl1abcXYZ-9"));
	}

	[Fact]
	public void OverlongDeviceId_IsRejected()
	{
		Assert.False(EasEndpoint.IsValidDeviceId(new string('a', 65)));
	}

	[Fact]
	public void DeviceIdWithIllegalCharacter_IsRejected()
	{
		Assert.False(EasEndpoint.IsValidDeviceId("bad id!"));
	}
}
