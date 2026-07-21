using System.Text;

namespace ActiveSync.Protocol.Http;

/// <summary>
///   Parsed /Microsoft-Server-ActiveSync query parameters (MS-ASHTTP 2.2.1.1.1).
///   Supports both the plain-text form (?Cmd=Sync&amp;User=...&amp;DeviceId=...) used up to 12.0
///   and the base64-encoded binary form used by protocol 12.1+ clients.
/// </summary>
public sealed record EasRequestParameters
{
	private static readonly string[] CommandCodes =
	[
		"Sync", // 0
		"SendMail", // 1
		"SmartForward", // 2
		"SmartReply", // 3
		"GetAttachment", // 4
		"GetHierarchy", // 5
		"CreateCollection", // 6
		"DeleteCollection", // 7
		"MoveCollection", // 8
		"FolderSync", // 9
		"FolderCreate", // 10
		"FolderDelete", // 11
		"FolderUpdate", // 12
		"MoveItems", // 13
		"GetItemEstimate", // 14
		"MeetingResponse", // 15
		"Search", // 16
		"Settings", // 17
		"Ping", // 18
		"ItemOperations", // 19
		"Provision", // 20
		"ResolveRecipients", // 21
		"ValidateCert", // 22
		"Find" // 23 (16.1)
	];

	/// <summary>
	///   The canonical spelling of <paramref name="command" /> (case-insensitive match against
	///   the MS-ASHTTP command set), or null when it is not an EAS command at all. The command
	///   arrives as client-controlled query text, so anything that becomes a metric label, a
	///   dictionary key or a dimension has to pass through here first.
	/// </summary>
	public static string? CanonicalCommand(string? command)
	{
		return string.IsNullOrEmpty(command)
			? null
			: Array.Find(CommandCodes, known => known.Equals(command, StringComparison.OrdinalIgnoreCase));
	}

	public required string Command { get; init; }
	public string ProtocolVersion { get; init; } = "14.1";
	public string DeviceId { get; init; } = "";
	public string DeviceType { get; init; } = "";
	public string? User { get; init; }
	public uint PolicyKey { get; init; }
	public string? AttachmentName { get; init; }
	public string? CollectionId { get; init; }
	public string? ItemId { get; init; }
	public string? LongId { get; init; }
	public string? Occurrence { get; init; }
	public bool SaveInSent { get; init; }
	public bool AcceptMultiPart { get; init; }

	/// <summary>Parses the plain-text query string form.</summary>
	public static EasRequestParameters FromQuery(IReadOnlyDictionary<string, string> query)
	{
		query.TryGetValue("Cmd", out string? cmd);
		return new EasRequestParameters
		{
			Command = cmd ?? throw new FormatException("Missing Cmd query parameter."),
			ProtocolVersion =
				"12.0", // plain-text query is only used by pre-12.1 clients; overridden by MS-ASProtocolVersion header
			DeviceId = query.GetValueOrDefault("DeviceId", ""),
			DeviceType = query.GetValueOrDefault("DeviceType", ""),
			User = query.GetValueOrDefault("User"),
			CollectionId = query.GetValueOrDefault("CollectionId"),
			ItemId = query.GetValueOrDefault("ItemId"),
			AttachmentName = query.GetValueOrDefault("AttachmentName"),
			LongId = query.GetValueOrDefault("LongId"),
			Occurrence = query.GetValueOrDefault("Occurrence"),
			SaveInSent = string.Equals(query.GetValueOrDefault("SaveInSent"), "T", StringComparison.OrdinalIgnoreCase)
		};
	}

	/// <summary>Parses the base64-encoded binary query value (MS-ASHTTP 2.2.1.1.1.1).</summary>
	public static EasRequestParameters FromBase64(string base64Query)
	{
		byte[] data;
		try
		{
			data = Convert.FromBase64String(base64Query);
		}
		catch (FormatException ex)
		{
			throw new FormatException("Query string is not valid base64.", ex);
		}

		int pos = 0;

		byte Next()
		{
			return pos < data.Length ? data[pos++] : throw new FormatException("Truncated base64 query.");
		}

		ReadOnlySpan<byte> NextSpan(int len)
		{
			if (pos + len > data.Length) throw new FormatException("Truncated base64 query.");
			Span<byte> span = data.AsSpan(pos, len);
			pos += len;
			return span;
		}

		byte versionByte = Next();
		string version = $"{versionByte / 10}.{versionByte % 10}";

		byte commandCode = Next();
		if (commandCode >= CommandCodes.Length)
			throw new FormatException($"Unknown EAS command code {commandCode}.");

		_ = NextSpan(2); // locale, unused

		byte deviceIdLength = Next();
		string deviceId = DecodeIdField(NextSpan(deviceIdLength));

		byte policyKeyLength = Next();
		// Per MS-ASHTTP the packed policy-key field is either absent (length 0) or 4 bytes.
		// Any other length would leave the cursor misaligned for the rest of the parse —
		// reject it as malformed (→ 400) rather than silently desynchronizing.
		if (policyKeyLength is not (0 or 4))
			throw new FormatException($"Invalid policy key length {policyKeyLength} (expected 0 or 4).");
		uint policyKey = policyKeyLength == 4
			? BitConverter.ToUInt32(NextSpan(4))
			: 0;

		byte deviceTypeLength = Next();
		string deviceType = Encoding.ASCII.GetString(NextSpan(deviceTypeLength));

		string? attachmentName = null, collectionId = null, itemId = null, longId = null, occurrence = null, user = null;
		bool saveInSent = false, acceptMultiPart = false;

		while (pos < data.Length)
		{
			byte tag = Next();
			byte length = Next();
			ReadOnlySpan<byte> value = NextSpan(length);
			switch (tag)
			{
				case 0: attachmentName = Encoding.UTF8.GetString(value); break;
				case 1: collectionId = Encoding.UTF8.GetString(value); break;
				case 2: break; // CollectionName (2.x only)
				case 3: itemId = Encoding.UTF8.GetString(value); break;
				case 4: longId = Encoding.UTF8.GetString(value); break;
				case 5: break; // ParentId (2.x only)
				case 6: occurrence = Encoding.UTF8.GetString(value); break;
				case 7:
					byte options = value.Length > 0 ? value[0] : (byte)0;
					saveInSent = (options & 0x01) != 0;
					acceptMultiPart = (options & 0x02) != 0;
					break;
				case 8: user = Encoding.UTF8.GetString(value); break;
			}
		}

		return new EasRequestParameters
		{
			Command = CommandCodes[commandCode],
			ProtocolVersion = version,
			DeviceId = deviceId,
			DeviceType = deviceType,
			User = user,
			PolicyKey = policyKey,
			AttachmentName = attachmentName,
			CollectionId = collectionId,
			ItemId = itemId,
			LongId = longId,
			Occurrence = occurrence,
			SaveInSent = saveInSent,
			AcceptMultiPart = acceptMultiPart
		};
	}

	/// <summary>
	///   Encodes these parameters as the MS-ASHTTP 2.2.1.1.1.1 base64 binary query value —
	///   the client-side counterpart of <see cref="FromBase64" /> (used by the test EAS client
	///   and round-trip tests).
	/// </summary>
	public string ToBase64()
	{
		int commandCode = Array.IndexOf(CommandCodes, Command);
		if (commandCode < 0)
			throw new ArgumentException($"Unknown EAS command '{Command}'.");
		string[] versionParts = ProtocolVersion.Split('.');
		byte versionByte = (byte)(int.Parse(versionParts[0]) * 10 +
		                          (versionParts.Length > 1 ? int.Parse(versionParts[1]) : 0));

		using MemoryStream ms = new();
		ms.WriteByte(versionByte);
		ms.WriteByte((byte)commandCode);
		ms.Write(BitConverter.GetBytes((ushort)0x0409)); // locale en-US

		byte[] deviceId = Encoding.ASCII.GetBytes(DeviceId);
		ms.WriteByte((byte)deviceId.Length);
		ms.Write(deviceId);

		if (PolicyKey != 0)
		{
			ms.WriteByte(4);
			ms.Write(BitConverter.GetBytes(PolicyKey));
		}
		else
		{
			ms.WriteByte(0);
		}

		byte[] deviceType = Encoding.ASCII.GetBytes(DeviceType);
		ms.WriteByte((byte)deviceType.Length);
		ms.Write(deviceType);

		void Param(byte tag, string? value)
		{
			if (string.IsNullOrEmpty(value))
				return;
			byte[] bytes = Encoding.UTF8.GetBytes(value);
			ms.WriteByte(tag);
			ms.WriteByte((byte)bytes.Length);
			ms.Write(bytes);
		}

		Param(0, AttachmentName);
		Param(1, CollectionId);
		Param(3, ItemId);
		Param(4, LongId);
		Param(6, Occurrence);
		if (SaveInSent || AcceptMultiPart)
		{
			ms.WriteByte(7);
			ms.WriteByte(1);
			ms.WriteByte((byte)((SaveInSent ? 0x01 : 0) | (AcceptMultiPart ? 0x02 : 0)));
		}

		Param(8, User);

		return Convert.ToBase64String(ms.ToArray());
	}

	/// <summary>
	///   Device IDs in the base64 form are raw bytes (often a GUID). Printable ASCII is kept as-is;
	///   anything else is hex-encoded, matching common gateway behavior.
	/// </summary>
	private static string DecodeIdField(ReadOnlySpan<byte> bytes)
	{
		bool printable = true;
		foreach (byte b in bytes)
			if (b < 0x20 || b > 0x7E)
			{
				printable = false;
				break;
			}

		return printable ? Encoding.ASCII.GetString(bytes) : Convert.ToHexString(bytes);
	}
}
