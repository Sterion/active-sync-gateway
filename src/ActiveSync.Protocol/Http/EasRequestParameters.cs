// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
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

	/// <summary>
	///   The protocol version bytes MS-ASHTTP defines (major × 10 + minor), oldest first. The byte
	///   is unauthenticated client input and the parsed version gates 16.x behaviour, so it is
	///   matched against this set rather than decoded arithmetically: 255 used to yield "25.5",
	///   which cleared every <c>&gt;= V160</c> / <c>&gt;= V161</c> check in the handlers. The
	///   command code immediately below has always been range-checked; this closes the same hole
	///   one field earlier. 2.5 and 12.0 are parsed but no longer advertised (see EasEndpoint).
	/// </summary>
	private static readonly byte[] ProtocolVersionBytes = [25, 120, 121, 140, 141, 160, 161];

	/// <summary>The EAS command this request invokes (e.g. "Sync", "SendMail", "Ping") — always one of <c>CommandCodes</c>.</summary>
	public required string Command { get; init; }

	/// <summary>The MS-ASProtocolVersion this request was parsed under, as "major.minor" (e.g. "14.1").</summary>
	public string ProtocolVersion { get; init; } = "14.1";

	/// <summary>The client-supplied device identifier (MS-ASHTTP DeviceId), used to key the persisted Device row.</summary>
	public string DeviceId { get; init; } = "";

	/// <summary>The client-supplied device type token (e.g. "iPhone", "WindowsOutlook15"), used for client-quirk detection.</summary>
	public string DeviceType { get; init; } = "";

	/// <summary>The user/mailbox identifier, when the client includes it in the query rather than relying solely on the HTTP auth header.</summary>
	public string? User { get; init; }

	/// <summary>
	///   The MS-ASPROV policy key the device is presenting. Compared against the persisted
	///   Device.PolicyKey (and the current policy document hash) to gate non-Provision commands
	///   with HTTP 449 when they disagree; zero when the device has not yet provisioned.
	/// </summary>
	public uint PolicyKey { get; init; }

	/// <summary>The FileReference for a legacy (pre-ItemOperations) GetAttachment request.</summary>
	public string? AttachmentName { get; init; }

	/// <summary>The target folder/collection id for commands that operate on a specific collection (Sync, ItemOperations, MoveItems, etc.).</summary>
	public string? CollectionId { get; init; }

	/// <summary>The target item id within <see cref="CollectionId" />, for commands that operate on a single item.</summary>
	public string? ItemId { get; init; }

	/// <summary>
	///   An opaque backend item reference ("{folderBackendKey}|{itemKey}"), as produced by Search
	///   results and resolved back by ItemOperations Fetch.
	/// </summary>
	public string? LongId { get; init; }

	/// <summary>The recurrence instance identifier, for commands that target a single occurrence of a recurring calendar item (e.g. MeetingResponse).</summary>
	public string? Occurrence { get; init; }

	/// <summary>Whether SendMail/SmartForward/SmartReply should save a copy of the outgoing message in Sent Items.</summary>
	public bool SaveInSent { get; init; }

	/// <summary>Whether the client accepts a multipart MIME response (as opposed to a single WBXML body) for commands like ItemOperations/GetAttachment.</summary>
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
		if (Array.IndexOf(ProtocolVersionBytes, versionByte) < 0)
			throw new FormatException($"Unknown EAS protocol version byte {versionByte}.");
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
			? BinaryPrimitives.ReadUInt32LittleEndian(NextSpan(4))
			: 0;

		// DeviceType is an ASCII token like "iPhone"/"WindowsOutlook15" -- the same shape as
		// DeviceId, so it goes through the same sanitizing boundary rather than a bare
		// Encoding.ASCII.GetString, which would pass C0 control characters (\r, \n, ESC) straight
		// through into a value persisted on the Device row and rendered in the admin UI/banner.
		byte deviceTypeLength = Next();
		string deviceType = DecodeIdField(NextSpan(deviceTypeLength));

		string? attachmentName = null, collectionId = null, itemId = null, longId = null, occurrence = null, user = null;
		bool saveInSent = false, acceptMultiPart = false;

		while (pos < data.Length)
		{
			byte tag = Next();
			byte length = Next();
			ReadOnlySpan<byte> value = NextSpan(length);
			switch (tag)
			{
				case 0: attachmentName = DecodeUtf8Field("AttachmentName", value); break;
				case 1: collectionId = DecodeUtf8Field("CollectionId", value); break;
				case 2: break; // CollectionName (2.x only)
				case 3: itemId = DecodeUtf8Field("ItemId", value); break;
				case 4: longId = DecodeUtf8Field("LongId", value); break;
				case 5: break; // ParentId (2.x only)
				case 6: occurrence = DecodeUtf8Field("Occurrence", value); break;
				case 7:
					byte options = value.Length > 0 ? value[0] : (byte)0;
					saveInSent = (options & 0x01) != 0;
					acceptMultiPart = (options & 0x02) != 0;
					break;
				case 8: user = DecodeUtf8Field("User", value); break;
				default:
					// An unknown tag means the cursor is either misaligned (a length byte read as a
					// tag) or the request is hand-crafted. Either way the remaining fields can no
					// longer be trusted, so reject it as malformed (→ 400) instead of parsing
					// "successfully" with silently wrong values.
					throw new FormatException($"Unknown query field tag {tag}.");
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
		byte versionByte = EncodeProtocolVersion(ProtocolVersion);

		Span<byte> multi = stackalloc byte[4];

		using MemoryStream ms = new();
		ms.WriteByte(versionByte);
		ms.WriteByte((byte)commandCode);
		BinaryPrimitives.WriteUInt16LittleEndian(multi, 0x0409); // locale en-US
		ms.Write(multi[..2]);

		byte[] deviceId = EncodeAsciiField(nameof(DeviceId), DeviceId);
		ms.WriteByte((byte)deviceId.Length);
		ms.Write(deviceId);

		if (PolicyKey != 0)
		{
			ms.WriteByte(4);
			BinaryPrimitives.WriteUInt32LittleEndian(multi, PolicyKey);
			ms.Write(multi);
		}
		else
		{
			ms.WriteByte(0);
		}

		byte[] deviceType = EncodeAsciiField(nameof(DeviceType), DeviceType);
		ms.WriteByte((byte)deviceType.Length);
		ms.Write(deviceType);

		void Param(byte tag, string? value, string fieldName)
		{
			if (string.IsNullOrEmpty(value))
				return;
			byte[] bytes = Encoding.UTF8.GetBytes(value);
			if (bytes.Length > MaxFieldBytes)
				throw new ArgumentException(
					$"{fieldName} is {bytes.Length} bytes, which exceeds the MS-ASHTTP 255-byte length-prefixed field limit.");
			ms.WriteByte(tag);
			ms.WriteByte((byte)bytes.Length);
			ms.Write(bytes);
		}

		Param(0, AttachmentName, nameof(AttachmentName));
		Param(1, CollectionId, nameof(CollectionId));
		Param(3, ItemId, nameof(ItemId));
		Param(4, LongId, nameof(LongId));
		Param(6, Occurrence, nameof(Occurrence));
		if (SaveInSent || AcceptMultiPart)
		{
			ms.WriteByte(7);
			ms.WriteByte(1);
			ms.WriteByte((byte)((SaveInSent ? 0x01 : 0) | (AcceptMultiPart ? 0x02 : 0)));
		}

		Param(8, User, nameof(User));

		return Convert.ToBase64String(ms.ToArray());
	}

	/// <summary>
	///   MS-ASHTTP 2.2.1.1.1.1 length-prefixed fields use a single length byte, so a value longer
	///   than this is unrepresentable -- it must be rejected, not silently wrapped/truncated.
	/// </summary>
	private const int MaxFieldBytes = 255;

	/// <summary>
	///   Encodes "major.minor" into the packed version byte, validated against the same
	///   <see cref="ProtocolVersionBytes" /> allowlist <see cref="FromBase64" /> reads with:
	///   an out-of-allowlist version (e.g. "15.0" -&gt; 150) is rejected here instead of emitting a
	///   byte FromBase64 refuses to read back, and the allowlist check runs on the pre-cast int
	///   value so an overflowing version (e.g. "28.1" -&gt; 281) cannot wrap into an allowed byte
	///   (281 wraps to 25, which decodes as "2.5") and be silently accepted as a different,
	///   wrong version. Parses with NumberStyles.None + InvariantCulture rather than the
	///   default culture-sensitive style, matching the repo's invariant-culture convention.
	/// </summary>
	private static byte EncodeProtocolVersion(string protocolVersion)
	{
		string[] versionParts = protocolVersion.Split('.');
		int major = 0, minor = 0;
		bool valid = versionParts.Length is 1 or 2
			&& int.TryParse(versionParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
			&& (versionParts.Length == 1 ||
			    int.TryParse(versionParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor));

		if (!valid)
			throw new ArgumentException($"Invalid protocol version '{protocolVersion}'.");

		int versionValue = major * 10 + minor;
		if (versionValue is < 0 or > byte.MaxValue || Array.IndexOf(ProtocolVersionBytes, (byte)versionValue) < 0)
			throw new ArgumentException($"Unknown EAS protocol version '{protocolVersion}'.");

		return (byte)versionValue;
	}

	/// <summary>
	///   Encodes <paramref name="value" /> for the fixed-position ASCII fields (DeviceId,
	///   DeviceType), rejecting both a non-ASCII character (the default <see cref="Encoding.ASCII" />
	///   silently maps anything outside the ASCII range to '?', which would round-trip to a
	///   DIFFERENT value with no error) and a value over the 255-byte length-prefix limit.
	/// </summary>
	private static byte[] EncodeAsciiField(string fieldName, string value)
	{
		Encoding strictAscii = Encoding.GetEncoding(
			"us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
		byte[] bytes;
		try
		{
			bytes = strictAscii.GetBytes(value);
		}
		catch (EncoderFallbackException ex)
		{
			throw new ArgumentException($"{fieldName} contains a non-ASCII character.", ex);
		}

		if (bytes.Length > MaxFieldBytes)
			throw new ArgumentException(
				$"{fieldName} is {bytes.Length} bytes, which exceeds the MS-ASHTTP 255-byte length-prefixed field limit.");
		return bytes;
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

	/// <summary>
	///   Decodes one of the UTF-8 tag-value fields (AttachmentName, CollectionId, ItemId, LongId,
	///   Occurrence, User), rejecting any decoded character <see cref="WireLog.IsUnsafe" /> flags
	///   (control characters, bidi-override/isolate format characters) rather than handing it
	///   straight to callers unfiltered -- these values flow into wire logs, the admin UI
	///   and, for CollectionId/ItemId, backend keys.
	/// </summary>
	private static string DecodeUtf8Field(string fieldName, ReadOnlySpan<byte> bytes)
	{
		string text = Encoding.UTF8.GetString(bytes);
		foreach (char c in text)
			if (WireLog.IsUnsafe(c, allowLineStructure: false))
				throw new FormatException($"{fieldName} contains an unsafe character.");
		return text;
	}
}
