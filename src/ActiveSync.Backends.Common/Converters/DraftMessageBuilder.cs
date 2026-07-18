using System.Xml.Linq;
using ActiveSync.Protocol.Wbxml;
using MimeKit;
using MimeKit.Text;

namespace ActiveSync.Backends.Converters;

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
			: existing?.Body is Multipart existingMultipart
				? existingMultipart.OfType<MimePart>().FirstOrDefault(p => p is TextPart)
				  ?? existingMultipart.FirstOrDefault()
				: existing?.Body;

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
		return message;
	}

	private static void FillAddresses(InternetAddressList target, string? payload, InternetAddressList? fallback)
	{
		if (payload is null)
		{
			if (fallback is not null)
				target.AddRange(fallback);
			return;
		}

		// EAS address lists are "a; b; c" (display forms allowed); unparsable entries are skipped.
		foreach (string entry in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (MailboxAddress.TryParse(entry, out MailboxAddress? address))
				target.Add(address);
	}

	private static List<MimeEntity> CollectAttachments(XElement applicationData, MimeMessage? existing)
	{
		XElement? attachmentsElement = applicationData.Element(ASB + "Attachments");
		List<MimeEntity> result = new();

		// Carried-over existing attachments, minus explicit deletes (matched by file name in
		// the FileReference tail — our mail FileReferences end in the attachment index, but
		// draft clients send back what we gave them; missing matches simply keep everything).
		HashSet<string> deletedNames = attachmentsElement?
			.Elements(ASB + "Delete")
			.Select(d => d.Element(ASB + "FileReference")?.Value ?? "")
			.Where(v => v.Length > 0)
			.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
		if (existing is not null)
			foreach (MimeEntity entity in existing.Attachments)
			{
				string name = entity.ContentDisposition?.FileName
				              ?? (entity as MimePart)?.FileName ?? "";
				if (!deletedNames.Any(d => d.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
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
			MimePart part = new("application", "octet-stream")
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
}
