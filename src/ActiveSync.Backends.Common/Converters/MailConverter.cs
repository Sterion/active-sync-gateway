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

namespace ActiveSync.Backends.Converters;

/// <summary>Converts MIME messages to EAS Email-class ApplicationData (MS-ASEMAIL / MS-ASAIRS).</summary>
public static class MailConverter
{
	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	public static List<XElement> ToApplicationData(
		MimeMessage message,
		MessageFlags flags,
		BodyPreference bodyPreference,
		Func<int, string> fileReferenceForAttachment)
	{
		List<XElement> data = new()
		{
			new XElement(Email + "To", Limit(message.To.ToString(), 32 * 1024)),
			new XElement(Email + "From", Limit(message.From.ToString(), 32 * 1024)),
			new XElement(Email + "Subject", message.Subject ?? ""),
			new XElement(Email + "DateReceived", EasDateTime.ToLong(message.Date.UtcDateTime)),
			new XElement(Email + "DisplayTo", string.Join("; ", message.To.Mailboxes.Select(m => m.Name ?? m.Address))),
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
			byte[] conversationId = MD5.HashData(Encoding.UTF8.GetBytes(conversationSeed));
			data.Add(Opaque(Email2 + "ConversationId", conversationId));
			// ConversationIndex header block (MS-OXOMSG 2.2.1.3): byte 0 is a reserved
			// marker, bytes 1-4 are the high 4 bytes of the message time as minutes since
			// 1601-01-01 UTC (big-endian). We emit only the 5-byte header (no per-reply
			// child blocks), which is enough for clients to thread by ConversationId.
			byte[] index = new byte[5];
			long minutes = (long)(message.Date.UtcDateTime - new DateTime(1601, 1, 1)).TotalMinutes;
			index[0] = 1;
			index[1] = (byte)(minutes >> 24);
			index[2] = (byte)(minutes >> 16);
			index[3] = (byte)(minutes >> 8);
			index[4] = (byte)minutes;
			data.Add(Opaque(Email2 + "ConversationIndex", index));
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

		content = content.Replace("\0", "");
		long estimated = Encoding.UTF8.GetByteCount(content);
		bool truncated = false;
		if (preference.TruncationSize is { } limit && estimated > limit)
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

		string? Prop(string name)
		{
			foreach (string rawLine in ics.Split('\n'))
			{
				string line = rawLine.TrimEnd('\r');
				if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
				    (line.Length == name.Length || line[name.Length] is ':' or ';'))
				{
					int colon = line.IndexOf(':');
					if (colon >= 0)
						return line[(colon + 1)..].Trim();
				}
			}

			return null;
		}

		string uid = Prop("UID") ?? Guid.NewGuid().ToString();
		DateTime? dtStart = ParseIcsDate(Prop("DTSTART"));
		DateTime? dtEnd = ParseIcsDate(Prop("DTEND"));
		string location = Prop("LOCATION") ?? "";
		string organizer = Prop("ORGANIZER")?.Replace("mailto:", "", StringComparison.OrdinalIgnoreCase) ?? "";
		bool allDay = Prop("DTSTART")?.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) == true;

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

	private static DateTime? ParseIcsDate(string? value)
	{
		if (string.IsNullOrEmpty(value))
			return null;
		value = value.Trim();
		string[] formats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd"];
		if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
			    DateTimeStyles.AssumeUniversal |
			    DateTimeStyles.AdjustToUniversal, out DateTime dt))
			return dt;
		return null;
	}

	private static long EstimateSize(MimePart part)
	{
		try
		{
			if (part.Content is null)
				return 0;
			using MemoryStream ms = new();
			part.Content.DecodeTo(ms);
			return ms.Length;
		}
		catch
		{
			return 0;
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

	private static string Limit(string value, int max)
	{
		return value.Length <= max ? value : value[..max];
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
