using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   Submission, looped back: a "sent" message is written straight into the sender's own Inbox.
/// </summary>
/// <remarks>
///   There is no MTA behind a directory tree, so delivery to anyone else is not something this
///   plugin can honestly implement. Looping the message back is the useful alternative — it makes
///   SendMail, SmartReply, SmartForward and the 16.x draft Send all observable end to end with no
///   mail server anywhere, and it is obvious what happened when the message reappears in the Inbox.
///   The host separately calls <c>IMailboxOperations.SaveToSentAsync</c>, so the same message also
///   lands in Sent, exactly as it would against a real backend.
/// </remarks>
internal sealed class LocalFilesMailSubmit(MailFolderTree tree, FileTreeWatcher watcher) : IMailSubmitOperations
{
	/// <inheritdoc />
	public async Task SendAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct)
	{
		DirectoryInfo inbox = tree.SpecialFolder(FolderType.Inbox)
		                      ?? throw new BackendException(
			                      "local-files: no Inbox to deliver into (CreateMissingFolders is off).");

		// Unread, like a freshly delivered message — a sent copy that arrived already read would be
		// invisible on most clients' unread counts and make the loopback hard to see.
		string fileName = MailFileName.Compose(ItemKeyMint.Mint(), new MailFlags(), [], out _);
		await AtomicFile.WriteAsync(Path.Combine(inbox.FullName, fileName), rfc822, ct).ConfigureAwait(false);
		watcher.NotifyChanged(inbox.FullName);
	}
}
