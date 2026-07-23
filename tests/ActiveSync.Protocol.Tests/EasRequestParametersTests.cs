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

	[Fact]
	public void Base64Query_UnknownVersionByte_IsRejected()
	{
		// W17: 255 decoded arithmetically as "25.5", which satisfies every >= V160 / >= V161
		// gate -- an unauthenticated caller unlocked 16.x behaviour it never negotiated.
		MemoryStream ms = new();
		ms.WriteByte(255);
		ms.WriteByte(0); // Sync
		ms.Write(BitConverter.GetBytes((ushort)0));
		ms.WriteByte(0); // no device id
		ms.WriteByte(0); // no policy key
		ms.WriteByte(0); // no device type

		Assert.Throws<FormatException>(
			() => EasRequestParameters.FromBase64(Convert.ToBase64String(ms.ToArray())));
	}

	[Fact]
	public void Base64Query_UnknownFieldTag_IsRejected()
	{
		// W4: the field-loop switch had no default case, so an unknown tag was silently
		// consumed (length-prefixed) and the parse "succeeded" with wrong/missing values --
		// a misaligned or hostile query hid its desync as success instead of a clean 400.
		MemoryStream ms = new();
		ms.WriteByte(141);
		ms.WriteByte(0); // Sync
		ms.Write(BitConverter.GetBytes((ushort)0));
		ms.WriteByte(0); // no device id
		ms.WriteByte(0); // no policy key
		ms.WriteByte(0); // no device type
		ms.WriteByte(99); // unknown field tag
		ms.WriteByte(1); // length
		ms.WriteByte(0x00); // value

		Assert.Throws<FormatException>(
			() => EasRequestParameters.FromBase64(Convert.ToBase64String(ms.ToArray())));
	}

	[Fact]
	public void Base64Query_MultiByteFields_AreLittleEndianOnTheWire()
	{
		// W2 COVERAGE (not red-first): MS-ASHTTP packs the locale and policy key little-endian,
		// but the codec read/wrote them with BitConverter (host endianness). On a little-endian
		// host (every CI/dev arm64/amd64 box) BitConverter and the little-endian primitives agree,
		// so this asserts the on-the-wire bytes are explicitly little-endian to guard the format
		// against a big-endian regression; it passes with and without the W2 fix on LE hardware.
		EasRequestParameters original = new()
		{
			Command = "Sync",
			ProtocolVersion = "16.1",
			DeviceId = "DEV1",
			DeviceType = "SP",
			PolicyKey = 0x01020304
		};

		byte[] wire = Convert.FromBase64String(original.ToBase64());

		// Layout: [version][command][locale:2][devIdLen][devId][policyLen][policyKey:4]...
		Assert.Equal(0x09, wire[2]); // locale 0x0409 low byte first
		Assert.Equal(0x04, wire[3]); // locale high byte second
		int policyKeyOffset = 4 + 1 + 4 /* devIdLen + "DEV1" */ + 1 /* policyLen */;
		Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, wire[policyKeyOffset..(policyKeyOffset + 4)]);

		// And the decode side reads that little-endian layout back to the same value.
		EasRequestParameters parsed = EasRequestParameters.FromBase64(original.ToBase64());
		Assert.Equal(0x01020304u, parsed.PolicyKey);
	}

	[Theory]
	[InlineData(25, "2.5")]
	[InlineData(120, "12.0")]
	[InlineData(121, "12.1")]
	[InlineData(140, "14.0")]
	[InlineData(141, "14.1")]
	[InlineData(160, "16.0")]
	[InlineData(161, "16.1")]
	public void Base64Query_DefinedVersionBytes_AreAccepted(byte versionByte, string expected)
	{
		MemoryStream ms = new();
		ms.WriteByte(versionByte);
		ms.WriteByte(0); // Sync
		ms.Write(BitConverter.GetBytes((ushort)0));
		ms.WriteByte(0);
		ms.WriteByte(0);
		ms.WriteByte(0);

		EasRequestParameters p = EasRequestParameters.FromBase64(Convert.ToBase64String(ms.ToArray()));

		Assert.Equal(expected, p.ProtocolVersion);
	}
}
