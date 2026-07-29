using ActiveSync.Contracts;
using MimeKit;

namespace ActiveSync.Backends.Jmap;

// Attachment fetch (ItemOperations) + the FileReference codec that folder/item key + attachment
// index round-trip through.
public sealed partial class JmapMailStore
{
	public async Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct)
	{
		string itemKey;
		int index;
		try
		{
			(_, itemKey, index) = ParseFileReference(fileReference);
		}
		catch (BackendException)
		{
			return null; // hand-crafted reference — same answer as a vanished attachment
		}

		string account = await AccountAsync(ct).ConfigureAwait(false);
		byte[]? raw = await GetRawByIdAsync(account, itemKey, ct).ConfigureAwait(false);
		if (raw is null)
			return null;
		using MemoryStream stream = new(raw);
		MimeMessage message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
		if (message.Attachments.Skip(index).FirstOrDefault() is not MimePart { Content: not null } part)
			return null;
		using MemoryStream output = new();
		await part.Content.DecodeToAsync(output, ct).ConfigureAwait(false);
		return new BackendAttachment { ContentType = part.ContentType.MimeType, Content = output.ToArray() };
	}

	public static string MakeFileReference(string folderBackendKey, string itemKey, int attachmentIndex)
	{
		return DelimitedKey.Encode(folderBackendKey, itemKey, attachmentIndex.ToString());
	}

	public static (string FolderBackendKey, string ItemKey, int AttachmentIndex) ParseFileReference(string fileReference)
	{
		string[]? parts = DelimitedKey.Decode(fileReference, 3);
		if (parts is null || !int.TryParse(parts[2], out int index) || index < 0)
			throw new BackendException("Malformed file reference.");
		return (parts[0], parts[1], index);
	}
}
