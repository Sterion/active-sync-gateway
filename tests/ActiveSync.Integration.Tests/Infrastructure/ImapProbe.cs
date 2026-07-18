using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace ActiveSync.Integration.Tests.Infrastructure;

/// <summary>
///   Direct-to-backend IMAP probes/mutations used by scenario tests to set up or verify state
///   out of band (i.e. not through the gateway). Each call is a fresh, short-lived connection.
/// </summary>
internal static class ImapProbe
{
	private static async Task<ImapClient> ConnectAsync(string user)
	{
		ImapClient imap = new();
		await imap.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None);
		await imap.AuthenticateAsync(user, TestBackend.Password);
		return imap;
	}

	public static async Task<bool> MessageHasFlagAsync(
		string user, string folder, string subject, MessageFlags flag)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder = await imap.GetFolderAsync(folder);
		await mailFolder.OpenAsync(FolderAccess.ReadOnly);
		IList<UniqueId> uids = await mailFolder.SearchAsync(SearchQuery.SubjectContains(subject));
		if (uids.Count == 0)
			return false;
		IList<IMessageSummary> summaries = await mailFolder.FetchAsync(uids, MessageSummaryItems.Flags);
		await imap.DisconnectAsync(true);
		return summaries.Any(s => (s.Flags ?? MessageFlags.None).HasFlag(flag));
	}

	public static async Task<bool> MessageExistsAsync(string user, string folder, string subject)
	{
		using ImapClient imap = await ConnectAsync(user);
		IMailFolder mailFolder;
		try
		{
			mailFolder = await imap.GetFolderAsync(folder);
		}
		catch (FolderNotFoundException)
		{
			return false; // a message cannot exist in a folder the server never created
		}

		await mailFolder.OpenAsync(FolderAccess.ReadOnly);
		IList<UniqueId> uids = await mailFolder.SearchAsync(SearchQuery.SubjectContains(subject));
		await imap.DisconnectAsync(true);
		return uids.Count > 0;
	}

	public static async Task SetSeenAsync(string user, string subject, bool seen)
	{
		using ImapClient imap = await ConnectAsync(user);
		await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
		IList<UniqueId> uids = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
		Assert.NotEmpty(uids);
		if (seen)
			await imap.Inbox.AddFlagsAsync(uids, MessageFlags.Seen, true);
		else
			await imap.Inbox.RemoveFlagsAsync(uids, MessageFlags.Seen, true);
		await imap.DisconnectAsync(true);
	}
}
