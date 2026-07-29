using ActiveSync.Protocol;

namespace ActiveSync.Server.Eas.Content;

/// <summary>
///   The wire-facing mail-attachment FileReference — "{folderBackendKey}|{itemKey}|{index}",
///   DelimitedKey-encoded. ENTIRELY host-internal since the typed item currency: the host emits
///   it when rendering a message's attachment list and parses it back in
///   ItemOperations/GetAttachment, then extracts the part from the raw RFC822 itself. A store
///   never sees a FileReference (the old store-side GetAttachmentAsync fetched the full message
///   and did the same extraction, so nothing was saved by round-tripping the reference through it).
///   The index is the position in MimeKit's <c>MimeMessage.Attachments</c> — host knowledge,
///   which is exactly why the reference must never cross the store boundary.
/// </summary>
internal static class MailFileReference
{
	public static string Encode(string folderBackendKey, string itemKey, int attachmentIndex)
	{
		// Per-component escaping so a '|' inside the folder key/name cannot be mis-parsed.
		return DelimitedKey.Encode(folderBackendKey, itemKey, attachmentIndex.ToString());
	}

	/// <summary>Parses a client-echoed reference; null for a malformed one (same answer as "not found").</summary>
	public static (string FolderBackendKey, string ItemKey, int AttachmentIndex)? TryParse(string fileReference)
	{
		string[]? parts = DelimitedKey.Decode(fileReference, 3);
		if (parts is null || !int.TryParse(parts[2], out int index) || index < 0)
			return null;
		return (parts[0], parts[1], index);
	}
}
