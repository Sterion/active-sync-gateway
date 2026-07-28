using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using MimeKit;
using MimeKit.Text;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>
///   Builds a MIME message from EAS 16.x draft ApplicationData (Sync Add/Change in the
///   Drafts folder). Change semantics are a merge: fields present in the payload replace
///   the stored draft's, absent fields survive from <c>existing</c>. Attachments arrive as
///   airsyncbase:Attachments &gt; Add(DisplayName, Content[base64]); existing attachments
///   are carried over unless the payload deletes them by ClientId/FileReference.
/// </summary>
public static class DraftMessageBuilder
{
	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	public static MimeMessage Build(XElement applicationData, MimeMessage? existing, string? fromAddress)
	{
		MimeMessage message = new();

		if (fromAddress is not null)
			message.From.Add(MailboxAddress.Parse(fromAddress));
		else if (existing?.From is { Count: > 0 })
			message.From.AddRange(existing.From);

		FillAddresses(message.To, applicationData.Element(Email + "To")?.Value, existing?.To);
		FillAddresses(message.Cc, applicationData.Element(Email + "Cc")?.Value, existing?.Cc);
		FillAddresses(message.Bcc, applicationData.Element(Email2 + "Bcc")?.Value, existing?.Bcc);

		message.Subject = applicationData.Element(Email + "Subject")?.Value
		                  ?? existing?.Subject ?? "";

		string? importance = applicationData.Element(Email + "Importance")?.Value;
		message.Importance = importance switch
		{
			"0" => MessageImportance.Low,
			"2" => MessageImportance.High,
			null => existing?.Importance ?? MessageImportance.Normal,
			_ => MessageImportance.Normal
		};

		// Body: replace when the payload carries one, keep the stored body otherwise.
		XElement? body = applicationData.Element(ASB + "Body");
		string? bodyData = body?.Element(ASB + "Data")?.Value;
		MimeEntity? textPart = bodyData is not null
			? new TextPart(body?.Element(ASB + "Type")?.Value == "2" ? TextFormat.Html : TextFormat.Plain)
			{
				Text = bodyData
			}
			: ExtractBodyEntity(existing?.Body);

		List<MimeEntity> attachments = CollectAttachments(applicationData, existing);
		if (attachments.Count == 0)
		{
			message.Body = textPart ?? new TextPart(TextFormat.Plain) { Text = "" };
		}
		else
		{
			Multipart multipart = new("mixed");
			multipart.Add(textPart ?? new TextPart(TextFormat.Plain) { Text = "" });
			foreach (MimeEntity attachment in attachments)
				multipart.Add(attachment);
			message.Body = multipart;
		}

		message.Date = DateTimeOffset.UtcNow;

		if (existing is not null)
			CarryOverHeaders(message, existing);

		return message;
	}

	/// <summary>
	///   A fresh <see cref="MimeMessage" /> starts with an empty header list; only
	///   From/To/Cc/Bcc/Subject/Importance/Date are rebuilt above from the payload/existing draft.
	///   Everything else on the stored draft — In-Reply-To, References, Message-Id, any custom
	///   header — must survive a Change or a reply thread started elsewhere (webmail) and merely
	///   touched on the phone gets sent as a brand-new thread. Skip the headers the properties
	///   above already write, so this never produces a duplicate.
	/// </summary>
	private static readonly HashSet<HeaderId> ManagedHeaders = new()
	{
		HeaderId.From, HeaderId.To, HeaderId.Cc, HeaderId.Bcc, HeaderId.Subject,
		HeaderId.Date, HeaderId.Importance, HeaderId.MimeVersion,
		HeaderId.ContentType, HeaderId.ContentTransferEncoding, HeaderId.ContentDisposition
	};

	private static void CarryOverHeaders(MimeMessage message, MimeMessage existing)
	{
		foreach (Header header in existing.Headers)
			if (!ManagedHeaders.Contains(header.Id))
				message.Headers.Add(header.Field, header.Value);
	}

	private static void FillAddresses(InternetAddressList target, string? payload, InternetAddressList? fallback)
	{
		if (payload is null)
		{
			if (fallback is not null)
				target.AddRange(fallback);
			return;
		}

		// Email:To/Cc (and Email2:Bcc) are RFC-5322 comma-separated address lists — this
		// repo's own emitter produces exactly that shape (MailConverter.cs, message.To.ToString()
		// -> "Alice" <a@x>, "Bob" <b@y>). Try the whole value as one list first; MimeKit's
		// InternetAddressList.TryParse handles both quoted display names and bare addresses.
		// Only fall back to the historical ';'-split (the DisplayTo convention, and some
		// clients' looser habit) when the comma-list parse fails outright.
		if (InternetAddressList.TryParse(payload, out InternetAddressList? list))
		{
			target.AddRange(list);
			return;
		}

		foreach (string entry in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (MailboxAddress.TryParse(entry, out MailboxAddress? address))
				target.Add(address);
	}

	private static List<MimeEntity> CollectAttachments(XElement applicationData, MimeMessage? existing)
	{
		XElement? attachmentsElement = applicationData.Element(ASB + "Attachments");
		List<MimeEntity> result = new();

		// Carried-over existing attachments, minus explicit deletes. Every FileReference we mint
		// is DelimitedKey(folderKey, itemKey, attachmentIndex), and that index counts into
		// MimeMessage.Attachments — the same order this loop walks — so match on the index.
		// Matching on the file-name tail instead treated an attachment with NO name (inline
		// images, message/rfc822 parts) as a suffix of every reference, so one unrelated
		// <Delete> dropped all of them. A reference we did not mint keeps everything.
		HashSet<int> deletedIndexes = attachmentsElement?
			.Elements(ASB + "Delete")
			.Select(d => ParseAttachmentIndex(d.Element(ASB + "FileReference")?.Value ?? ""))
			.Where(i => i >= 0)
			.ToHashSet() ?? [];
		if (existing is not null)
		{
			int index = 0;
			foreach (MimeEntity entity in existing.Attachments)
				if (!deletedIndexes.Contains(index++))
					result.Add(entity);
		}

		if (attachmentsElement is null)
			return result;

		foreach (XElement add in attachmentsElement.Elements(ASB + "Add"))
		{
			string? content = add.Element(ASB + "Content")?.Value;
			if (content is null)
				continue;
			byte[] bytes;
			try
			{
				bytes = Convert.FromBase64String(content);
			}
			catch (FormatException)
			{
				continue; // a malformed part must not sink the whole draft
			}

			string displayName = add.Element(ASB + "DisplayName")?.Value ?? "attachment";

			// Honour the client's ContentType; fall back to the name's type, then octet-stream.
			// Typing every attachment octet-stream makes a phone photo unopenable at the far end.
			string? declared = add.Element(ASB + "ContentType")?.Value;
			if (string.IsNullOrWhiteSpace(declared)
			    || !ContentType.TryParse(declared, out ContentType? contentType))
				contentType = ContentType.Parse(MimeTypes.GetMimeType(displayName));

			MimePart part = new(contentType)
			{
				Content = new MimeContent(new MemoryStream(bytes)),
				ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
				{
					FileName = displayName
				},
				ContentTransferEncoding = ContentEncoding.Base64,
				FileName = displayName
			};
			result.Add(part);
		}

		return result;
	}

	/// <summary>
	///   The stored draft's body, minus the attachments <see cref="CollectAttachments" /> carries
	///   over separately. Everything that is not an attachment stays intact: a
	///   multipart/alternative keeps its text/html sibling (picking the first TextPart out of it
	///   downgraded a rich draft to plain text on every flag-only edit) and a multipart/related
	///   keeps the inline parts nothing else would carry.
	/// </summary>
	private static MimeEntity? ExtractBodyEntity(MimeEntity? body)
	{
		if (body is not Multipart multipart || body.IsAttachment)
			return body;

		// alternative/related ARE the body — descending into them is what loses siblings.
		if (!multipart.ContentType.IsMimeType("multipart", "mixed"))
			return multipart;

		foreach (MimeEntity child in multipart)
			if (!child.IsAttachment)
				return ExtractBodyEntity(child);

		return null;
	}

	/// <summary>
	///   The trailing attachment index of a mail FileReference — the shape both mail backends
	///   mint via <c>DelimitedKey.Encode(folderKey, itemKey, index)</c>. Returns -1 for anything
	///   else, which the caller reads as "matches nothing" rather than guessing at a target.
	/// </summary>
	private static int ParseAttachmentIndex(string fileReference)
	{
		string[]? parts = DelimitedKey.Decode(fileReference, 3);
		return parts is not null && int.TryParse(parts[2], out int index) ? index : -1;
	}
}
