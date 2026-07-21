using System.Text;
using ActiveSync.Protocol.Http;

namespace ActiveSync.Protocol.Tests;

public class EasRequestParametersTests
{
	[Fact]
	public void PlainQuery_Parses()
	{
		Dictionary<string, string> query = new()
		{
			["Cmd"] = "Sync",
			["User"] = "user@example.com",
			["DeviceId"] = "ABC123",
			["DeviceType"] = "iPhone"
		};

		EasRequestParameters p = EasRequestParameters.FromQuery(query);

		Assert.Equal("Sync", p.Command);
		Assert.Equal("ABC123", p.DeviceId);
		Assert.Equal("iPhone", p.DeviceType);
		Assert.Equal("user@example.com", p.User);
	}

	[Fact]
	public void Base64Query_Parses()
	{
		// Build a synthetic MS-ASHTTP base64 query: v14.1, FolderSync(9), locale, deviceId "DEV1",
		// 4-byte policy key 1234, deviceType "SP", options param with SaveInSent
		MemoryStream ms = new();
		ms.WriteByte(141); // protocol version 14.1
		ms.WriteByte(9); // FolderSync
		ms.Write(BitConverter.GetBytes((ushort)0x0409)); // locale en-US
		byte[] dev = Encoding.ASCII.GetBytes("DEV1");
		ms.WriteByte((byte)dev.Length);
		ms.Write(dev);
		ms.WriteByte(4);
		ms.Write(BitConverter.GetBytes(1234u));
		byte[] devType = Encoding.ASCII.GetBytes("SP");
		ms.WriteByte((byte)devType.Length);
		ms.Write(devType);
		ms.WriteByte(7); // Options tag
		ms.WriteByte(1);
		ms.WriteByte(0x01); // SaveInSent

		EasRequestParameters p = EasRequestParameters.FromBase64(Convert.ToBase64String(ms.ToArray()));

		Assert.Equal("FolderSync", p.Command);
		Assert.Equal("14.1", p.ProtocolVersion);
		Assert.Equal("DEV1", p.DeviceId);
		Assert.Equal("SP", p.DeviceType);
		Assert.Equal(1234u, p.PolicyKey);
		Assert.True(p.SaveInSent);
	}

	[Fact]
	public void ToBase64_RoundTripsThroughFromBase64()
	{
		EasRequestParameters original = new()
		{
			Command = "SmartForward",
			ProtocolVersion = "14.1",
			DeviceId = "TESTDEVICE01",
			DeviceType = "TestClient",
			PolicyKey = 987654321,
			CollectionId = "5",
			ItemId = "5:42",
			SaveInSent = true,
			AcceptMultiPart = false
		};

		EasRequestParameters parsed = EasRequestParameters.FromBase64(original.ToBase64());

		Assert.Equal(original.Command, parsed.Command);
		Assert.Equal(original.ProtocolVersion, parsed.ProtocolVersion);
		Assert.Equal(original.DeviceId, parsed.DeviceId);
		Assert.Equal(original.DeviceType, parsed.DeviceType);
		Assert.Equal(original.PolicyKey, parsed.PolicyKey);
		Assert.Equal(original.CollectionId, parsed.CollectionId);
		Assert.Equal(original.ItemId, parsed.ItemId);
		Assert.True(parsed.SaveInSent);
		Assert.False(parsed.AcceptMultiPart);
	}

	[Fact]
	public void Base64Query_InvalidPolicyKeyLength_IsRejected()
	{
		// A policy-key length of 2 (neither 0 nor 4) would desync the rest of the parse —
		// it must be rejected as a FormatException (→ HTTP 400), not silently mis-parsed.
		MemoryStream ms = new();
		ms.WriteByte(141);
		ms.WriteByte(9); // FolderSync
		ms.Write(BitConverter.GetBytes((ushort)0));
		byte[] dev = Encoding.ASCII.GetBytes("DEV1");
		ms.WriteByte((byte)dev.Length);
		ms.Write(dev);
		ms.WriteByte(2); // invalid policy-key length
		ms.Write([0x01, 0x02]);
		ms.WriteByte(0); // device type length

		Assert.Throws<FormatException>(() =>
			EasRequestParameters.FromBase64(Convert.ToBase64String(ms.ToArray())));
	}

	[Fact]
	public void Base64Query_BinaryDeviceId_IsHexEncoded()
	{
		MemoryStream ms = new();
		ms.WriteByte(141);
		ms.WriteByte(18); // Ping
		ms.Write(BitConverter.GetBytes((ushort)0));
		byte[] guidBytes = Guid.NewGuid().ToByteArray();
		ms.WriteByte((byte)guidBytes.Length);
		ms.Write(guidBytes);
		ms.WriteByte(0); // no policy key
		ms.WriteByte(0); // no device type

		EasRequestParameters p = EasRequestParameters.FromBase64(Convert.ToBase64String(ms.ToArray()));

		Assert.Equal("Ping", p.Command);
		Assert.Equal(Convert.ToHexString(guidBytes), p.DeviceId);
	}

	[Theory]
	[InlineData("Sync", "Sync")]
	[InlineData("fOlDeRsYnC", "FolderSync")]
	[InlineData("Find", "Find")]
	public void CanonicalCommand_KnownCommand_FoldsToTheCanonicalCasing(string input, string expected)
	{
		Assert.Equal(expected, EasRequestParameters.CanonicalCommand(input));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("Syncc")]
	[InlineData("<script>alert(1)</script>")]
	public void CanonicalCommand_UnknownCommand_IsNull(string? input)
	{
		Assert.Null(EasRequestParameters.CanonicalCommand(input));
	}
}
