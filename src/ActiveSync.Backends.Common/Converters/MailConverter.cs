using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>Converts MIME messages to EAS Email-class ApplicationData (MS-ASEMAIL / MS-ASAIRS).</summary>
public static class MailConverter
{
	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	/// <param name="receivedUtc">
	///   The backend's own delivery timestamp — IMAP INTERNALDATE or JMAP receivedAt — preferred
	///   over the sender-supplied Date: header for MS-ASEMAIL DateReceived (D14). Null when the
	///   caller has none; falls back to <paramref name="message" />'s Date header, and only to that
	///   when it is itself non-default (a parsed message with no Date: header reports
	///   <c>default(DateTimeOffset)</c>, i.e. year 0001 — never emitted verbatim).
	/// </param>
	public static List<XElement> ToApplicationData(
		MimeMessage message,
		MessageFlags flags,
		BodyPreference bodyPreference,
		Func<int, string> fileReferenceForAttachment,
		DateTimeOffset? receivedUtc = null)
	{
		DateTimeOffset received = receivedUtc ?? (message.Date != default ? message.Date : DateTimeOffset.UtcNow);
		List<XElement> data = new()
		{
			new XElement(Email + "To", Limit(message.To.ToString(), 32 * 1024)),
			new XElement(Email + "From", Limit(message.From.ToString(), 32 * 1024)),
			new XElement(Email + "Subject", message.Subject ?? ""),
			new XElement(Email + "DateReceived", EasDateTime.ToLong(received.UtcDateTime)),
			new XElement(Email + "DisplayTo", string.Join("; ",
				message.To.Mailboxes.Select(m => string.IsNullOrEmpty(m.Name) ? m.Address : m.Name))),
			new XElement(Email + "ThreadTopic", NormalizeTopic(message.Subject)),
			new XElement(Email + "Importance", message.Priority switch
			{
				MessagePriority.Urgent => "2",
				MessagePriority.NonUrgent => "0",
				_ => "1"
			}),
			new XElement(Email + "Read", flags.Read ? "1" : "0")
		};

		if (message.Cc.Count > 0)
			data.Add(new XElement(Email + "Cc", Limit(message.Cc.ToString(), 32 * 1024)));
		if (message.ReplyTo.Count > 0)
			data.Add(new XElement(Email + "ReplyTo", Limit(message.ReplyTo.ToString(), 1024)));

		XElement? attachments = BuildAttachments(message, fileReferenceForAttachment);
		if (attachments is not null)
			data.Add(attachments);

		data.Add(BuildBody(message, bodyPreference, out int nativeBodyType));

		XElement? meetingRequest = BuildMeetingRequest(message);
		if (meetingRequest is not null)
		{
			data.Add(meetingRequest);
			data.Add(new XElement(Email + "MessageClass", "IPM.Schedule.Meeting.Request"));
			data.Add(new XElement(Email + "ContentClass", "urn:content-classes:calendarmessage"));
		}
		else
		{
			data.Add(new XElement(Email + "MessageClass", "IPM.Note"));
			data.Add(new XElement(Email + "ContentClass", "urn:content-classes:message"));
		}

		data.Add(new XElement(Email + "InternetCPID", "65001"));

		// Flag block (MS-ASEMAIL Flag: 2 = flagged, 1 = complete, 0 = cleared)
		XElement flag = new(Email + "Flag");
		if (flags.Flagged)
		{
			flag.Add(new XElement(Email + "Status", "2"));
			flag.Add(new XElement(Email + "FlagType", "for Follow Up"));
		}

		data.Add(flag);

		// User categories = the message's custom IMAP keywords, minus the system ones.
		IReadOnlyList<string> categories = CategoryKeywords(flags.Keywords);
		if (categories.Count > 0)
			data.Add(new XElement(Email + "Categories",
				categories.Select(c => new XElement(Email + "Category", c))));

		data.Add(new XElement(AirSyncBase + "NativeBodyType", nativeBodyType.ToString()));

		// Conversation grouping (protocol 14.x): derive stable ids from threading headers.
		string conversationSeed = message.References.FirstOrDefault() ?? NormalizeTopic(message.Subject);
		if (!string.IsNullOrEmpty(conversationSeed))
		{
			// D23: ConversationIndex used to be written here as a 5-byte stub, 17 bytes short of
			// the MS-OXOMSG 2.2.1.3 22-byte header (5 time bytes + a 16-byte GUID) and with its own
			// comment contradicting the bytes it wrote (claimed the high 4 bytes of the message
			// time, wrote the low 32 bits). No real client can thread on a shape that is neither —
			// ConversationId alone is correct and sufficient for clients to thread by, so that is
			// the only conversation-grouping element emitted.
			byte[] conversationId = MD5.HashData(Encoding.UTF8.GetBytes(conversationSeed));
			data.Add(Opaque(Email2 + "ConversationId", conversationId));
		}

		if (flags.Answered)
			data.Add(new XElement(Email2 + "LastVerbExecuted", "1")); // REPLYTOSENDER
		else if (flags.Forwarded) data.Add(new XElement(Email2 + "LastVerbExecuted", "3")); // FORWARD

		return data;
	}

	public static XElement BuildBody(MimeMessage message, BodyPreference preference, out int nativeBodyType)
	{
		string? html = message.HtmlBody;
		string? text = message.TextBody;
		nativeBodyType = html is not null ? 2 : 1;

		string content;
		int type;
		switch (preference.Type)
		{
			case 4: // full MIME
				using (MemoryStream ms = new())
				{
					// D15: a serialized RFC 822 stream is a byte stream, not UTF-8 text --
					// stringifying it via Encoding.UTF8.GetString mangles any 8-bit/non-UTF-8
					// part (invalid sequences become U+FFFD) and the NUL-strip below then
					// corrupts any part carrying raw bytes (Content-Transfer-Encoding: binary).
					// Prepare(SevenBit) constrains every part's transfer encoding to something
					// ASCII-safe (quoted-printable/base64) BEFORE writing, so the serialized
					// bytes are valid ASCII by construction and neither transformation can lose
					// or corrupt anything.
					message.Prepare(EncodingConstraint.SevenBit, FormatOptions.Default.MaxLineLength);
					message.WriteTo(ms);
					content = Encoding.UTF8.GetString(ms.ToArray());
				}

				type = 4;
				break;
			case 2 when html is not null:
				content = html;
				type = 2;
				break;
			default:
				content = text ?? HtmlToText(html) ?? "";
				type = 1;
				break;
		}

		// D15: only the plain-text/HTML branches can carry a stray NUL from HTML-to-text
		// conversion or a permissive backend; type 4 is now ASCII-safe by construction (Prepare
		// above) and stripping NULs from it would corrupt a legitimately NUL-bearing binary part.
		if (type != 4)
			content = content.Replace("\0", "");
		long estimated = Encoding.UTF8.GetByteCount(content);
		bool truncated = false;
		// D4: type 4 is the serialized message/rfc822 stream — cutting it at an arbitrary byte
		// offset lands mid-header or mid-part and hands the client unparsable MIME (base64
		// attachment parts split mid-line, headers truncated). A MIME fetch is all-or-nothing,
		// so it is exempt from the TruncationSize byte budget the plain-text/HTML bodies honor.
		if (type != 4 && preference.TruncationSize is { } limit && estimated > limit)
		{
			content = BodyText.TruncateUtf8(content, limit);
			truncated = true;
		}

		XElement body = new(AirSyncBase + "Body",
			new XElement(AirSyncBase + "Type", type.ToString()),
			new XElement(AirSyncBase + "EstimatedDataSize", estimated.ToString()),
			new XElement(AirSyncBase + "Truncated", truncated ? "1" : "0"));
		if (!truncated || content.Length > 0)
			body.Add(new XElement(AirSyncBase + "Data", content));
		return body;
	}

	private static XElement? BuildAttachments(MimeMessage message, Func<int, string> fileReferenceFor)
	{
		List<XElement> list = new();
		int index = 0;
		foreach (MimeEntity entity in message.Attachments)
		{
			string name = entity.ContentDisposition?.FileName
			              ?? entity.ContentType.Name
			              ?? $"attachment{index}";
			long size = entity is MimePart part ? EstimateSize(part) : 0;
			bool isInline = entity.ContentDisposition?.Disposition == ContentDisposition.Inline
			                || entity.ContentId is not null;

			XElement att = new(AirSyncBase + "Attachment",
				new XElement(AirSyncBase + "DisplayName", name),
				new XElement(AirSyncBase + "FileReference", fileReferenceFor(index)),
				new XElement(AirSyncBase + "Method", "1"),
				new XElement(AirSyncBase + "EstimatedDataSize", size.ToString()));
			if (isInline && entity.ContentId is not null)
			{
				att.Add(new XElement(AirSyncBase + "ContentId", entity.ContentId));
				att.Add(new XElement(AirSyncBase + "IsInline", "1"));
			}

			list.Add(att);
			index++;
		}

		return list.Count > 0 ? new XElement(AirSyncBase + "Attachments", list) : null;
	}

	private static XElement? BuildMeetingRequest(MimeMessage message)
	{
		MimePart? calendarPart = message.BodyParts.OfType<MimePart>()
			.FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar"));
		if (calendarPart?.Content is null)
			return null;

		string ics;
		using (MemoryStream ms = new())
		{
			calendarPart.Content.DecodeTo(ms);
			ics = Encoding.UTF8.GetString(ms.ToArray());
		}

		if (!ics.Contains("METHOD:REQUEST", StringComparison.OrdinalIgnoreCase))
			return null;

		// Unfold RFC 5545 §3.1 continuation lines (a CRLF followed by a space or tab) before
		// scanning properties, otherwise a folded LOCATION/ORGANIZER is silently truncated.
		string unfolded = ics
			.Replace("\r\n ", "").Replace("\r\n\t", "")
			.Replace("\n ", "").Replace("\n\t", "");

		// D1: Outlook/Google/Exchange all emit BEGIN:VTIMEZONE before BEGIN:VEVENT, and its
		// STANDARD/DAYLIGHT subcomponents each carry a bare (no-Z, no-TZID) DTSTART for the
		// 1970 DST transition. Scanning the whole ICS from the top let that line be mistaken
		// for the real VEVENT DTSTART, so restrict every property lookup to the VEVENT block.
		int veventStart = unfolded.IndexOf("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase);
		int veventEnd = veventStart >= 0
			? unfolded.IndexOf("END:VEVENT", veventStart, StringComparison.OrdinalIgnoreCase)
			: -1;
		string scanText = veventStart >= 0 && veventEnd > veventStart
			? unfolded[veventStart..veventEnd]
			: unfolded;

		// Returns the property's parameter segment (everything between the name and the first
		// colon, e.g. ";TZID=Europe/Copenhagen") and its value (after the colon).
		(string Parameters, string Value)? Prop(string name)
		{
			foreach (string rawLine in scanText.Split('\n'))
			{
				string line = rawLine.TrimEnd('\r');
				if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
				    (line.Length == name.Length || line[name.Length] is ':' or ';'))
				{
					int colon = line.IndexOf(':');
					if (colon >= 0)
						return (line[name.Length..colon], line[(colon + 1)..].Trim());
				}
			}

			return null;
		}

		string uid = Prop("UID")?.Value ?? Guid.NewGuid().ToString();
		(string Parameters, string Value)? startProp = Prop("DTSTART");
		(string Parameters, string Value)? endProp = Prop("DTEND");
		DateTime? dtStart = ParseIcsDate(startProp);
		DateTime? dtEnd = ParseIcsDate(endProp);
		string location = Prop("LOCATION")?.Value ?? "";
		string organizer = Prop("ORGANIZER")?.Value.Replace("mailto:", "", StringComparison.OrdinalIgnoreCase) ?? "";
		bool allDay = startProp is { } sp &&
		              (sp.Parameters.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) ||
		               (sp.Value.Length == 8 && !sp.Value.Contains('T')));

		XElement mr = new(Email + "MeetingRequest",
			new XElement(Email + "AllDayEvent", allDay ? "1" : "0"),
			new XElement(Email + "StartTime", EasDateTime.ToLong(dtStart ?? DateTime.UtcNow)),
			new XElement(Email + "DtStamp", EasDateTime.ToLong(DateTime.UtcNow)),
			new XElement(Email + "EndTime", EasDateTime.ToLong(dtEnd ?? (dtStart ?? DateTime.UtcNow).AddHours(1))),
			new XElement(Email + "InstanceType", "0"),
			new XElement(Email + "Location", location),
			new XElement(Email + "Organizer", organizer),
			new XElement(Email + "ResponseRequested", "1"),
			new XElement(Email + "Sensitivity", "0"),
			new XElement(Email + "BusyStatus", "2"),
			new XElement(Email + "TimeZone", TimeZoneBlob.UtcBase64),
			new XElement(Email + "GlobalObjId", EncodeGlobalObjId(uid)));
		return mr;
	}

	/// <summary>Encodes an iCalendar UID as an Outlook GlobalObjId (vCal-Uid wrapper), base64.</summary>
	public static string EncodeGlobalObjId(string uid)
	{
		// The 16-byte class-id (0x04000000 82 00 E0 00 74 C5 B7 10 1A 82 E0 08) followed by
		// 16 zero bytes is the fixed MS-OXOCAL Global Object ID header. A UID that did not
		// originate as a Windows GOID is carried by appending the "vCal-Uid" marker + the
		// raw UID text (below), which Outlook round-trips verbatim.
		byte[] header =
		[
			0x04, 0x00, 0x00, 0x00, 0x82, 0x00, 0xE0, 0x00,
			0x74, 0xC5, 0xB7, 0x10, 0x1A, 0x82, 0xE0, 0x08,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
		];
		byte[] marker = "vCal-Uid\x01\x00\x00\x00"u8.ToArray();
		byte[] uidBytes = Encoding.UTF8.GetBytes(uid);
		int dataLength = marker.Length + uidBytes.Length + 1;
		using MemoryStream ms = new();
		ms.Write(header);
		ms.Write(BitConverter.GetBytes(dataLength));
		ms.Write(marker);
		ms.Write(uidBytes);
		ms.WriteByte(0);
		return Convert.ToBase64String(ms.ToArray());
	}

	private static DateTime? ParseIcsDate((string Parameters, string Value)? property)
	{
		if (property is not { } prop || string.IsNullOrEmpty(prop.Value))
			return null;
		string value = prop.Value.Trim();

		// Bare-Z (UTC) or DATE forms are already absolute.
		if (DateTime.TryParseExact(value, ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd"],
			    CultureInfo.InvariantCulture,
			    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime absolute))
			return absolute;

		// A floating (no-Z) wall-clock time: honour the TZID parameter if present, otherwise
		// fall back to treating it as UTC (the historical behaviour).
		if (DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
			    DateTimeStyles.None, out DateTime local))
		{
			TimeZoneInfo? zone = ResolveTzid(prop.Parameters);
			if (zone is not null)
			{
				try
				{
					return TimeZoneInfo.ConvertTimeToUtc(
						DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
				}
				catch (Exception ex) when (ex is ArgumentException or InvalidTimeZoneException)
				{
					// ambiguous/invalid wall-clock in the zone — fall through to UTC
				}
			}

			return DateTime.SpecifyKind(local, DateTimeKind.Utc);
		}

		return null;
	}

	/// <summary>Reads a TZID= parameter and resolves it to a <see cref="TimeZoneInfo" />.</summary>
	private static TimeZoneInfo? ResolveTzid(string parameters)
	{
		foreach (string part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			if (!part.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
				continue;
			string id = part[5..].Trim();
			if (id.Length == 0)
				return null;
			if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone))
				return zone;
			// iCalendar TZIDs are frequently Windows ids; try the IANA translation.
			if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out string? iana) &&
			    TimeZoneInfo.TryFindSystemTimeZoneById(iana, out zone))
				return zone;
			return null;
		}

		return null;
	}

	private static long EstimateSize(MimePart part)
	{
		try
		{
			if (part.Content is null)
				return 0;
			// D16: decoding into a MemoryStream just to read its Length materializes the whole
			// attachment in memory, per message per windowed Sync batch. Count the decoded bytes
			// through a write-only sink instead -- DecodeTo still runs the transfer decoder, but
			// nothing beyond its own small internal buffer is ever held at once.
			using CountingStream counter = new();
			part.Content.DecodeTo(counter);
			return counter.Length;
		}
		catch
		{
			return 0;
		}
	}

	/// <summary>
	///   A write-only sink that counts bytes without buffering them (D16) -- used to size a MIME
	///   part's decoded content without materializing it in memory.
	/// </summary>
	private sealed class CountingStream : Stream
	{
		private long length;

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => length;
		public override long Position { get => length; set { } }

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count)
		{
			length += count;
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			length += buffer.Length;
		}
	}

	private static string NormalizeTopic(string? subject)
	{
		if (string.IsNullOrEmpty(subject))
			return "";
		string topic = subject;
		while (true)
		{
			string trimmed = topic.TrimStart();
			if (trimmed.StartsWith("RE:", StringComparison.OrdinalIgnoreCase))
				topic = trimmed[3..];
			else if (trimmed.StartsWith("FW:", StringComparison.OrdinalIgnoreCase))
				topic = trimmed[3..];
			else if (trimmed.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase))
				topic = trimmed[4..];
			else
				return trimmed;
		}
	}

	private static string? HtmlToText(string? html)
	{
		if (html is null)
			return null;
		string text = Regex.Replace(html, "<[^>]+>", " ");
		return WebUtility.HtmlDecode(text);
	}

	/// <summary>
	///   D25 — a naive `value[..max]` can cut a UTF-16 surrogate pair in half, producing a lone
	///   surrogate that <see cref="System.Xml.XmlWriter" /> rejects when the response is encoded
	///   -- sinking the whole Sync response over one oversized header rather than one message.
	///   Route through the byte-budgeted, code-point-aligned truncation the body text already
	///   uses; <paramref name="max" /> is treated as a byte, not a character, budget (the two
	///   coincide for the ASCII-heavy header text this is used on).
	/// </summary>
	private static string Limit(string value, int max)
	{
		return BodyText.TruncateUtf8(value, max);
	}

	private static XElement Opaque(XName name, byte[] data)
	{
		XElement element = new(name, Convert.ToBase64String(data));
		element.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		return element;
	}

	// Managed/system keywords that must never surface as user categories (nor be removed
	// by a client clearing its category list). Everything backslash-prefixed is an IMAP
	// system flag by definition.
	private static readonly HashSet<string> SystemKeywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"$Forwarded", "$MDNSent", "$SubmitPending", "$Submitted",
		"$Junk", "$NotJunk", "Junk", "NonJunk", "$Phishing"
	};

	/// <summary>
	///   The category-relevant subset of a message's IMAP keywords: system keywords
	///   filtered out, sorted for stable revision strings.
	/// </summary>
	public static IReadOnlyList<string> CategoryKeywords(IEnumerable<string>? keywords)
	{
		if (keywords is null)
			return [];
		return keywords
			.Where(k => k.Length > 0 && k[0] != '\\' && !SystemKeywords.Contains(k))
			.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public sealed record MessageFlags(
		bool Read, bool Flagged, bool Answered, bool Forwarded,
		IReadOnlyCollection<string>? Keywords = null);
}
