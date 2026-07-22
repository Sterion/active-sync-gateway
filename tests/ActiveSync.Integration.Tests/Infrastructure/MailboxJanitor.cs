using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Integration.Tests.Infrastructure;

/// <summary>
///   Cleans backend state directly (bypassing the gateway) so tests start from a known-ish
///   baseline. Tests still use GUID subjects/ids — cleanup is best-effort, not a guarantee.
/// </summary>
public static class MailboxJanitor
{
	public static async Task PurgeMailAsync(string user, CancellationToken ct = default)
	{
		using ImapClient client = new();
		await client.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
		await client.AuthenticateAsync(user, TestBackend.Password, ct);

		IMailFolder personal = client.GetFolder(client.PersonalNamespaces[0]);
		List<IMailFolder> folders = new() { client.Inbox };
		folders.AddRange(await personal.GetSubfoldersAsync(true, ct));

		foreach (IMailFolder folder in folders.DistinctBy(f => f.FullName))
			try
			{
				if (folder.Attributes.HasFlag(FolderAttributes.NonExistent) ||
				    folder.Attributes.HasFlag(FolderAttributes.NoSelect))
					continue;
				await folder.OpenAsync(FolderAccess.ReadWrite, ct);
				if (folder.Count > 0)
				{
					await folder.AddFlagsAsync(
						Enumerable.Range(0, folder.Count).ToList(), MessageFlags.Deleted, true, ct);
					await folder.ExpungeAsync(ct);
				}
			}
			catch
			{
				// best effort
			}

		await client.DisconnectAsync(true, ct);
	}

	public static async Task PurgeDavAsync(string user, CancellationToken ct = default)
	{
		if (TestBackend.DavUrl is not { } davUrl)
			return;
		BackendCredentials credentials = new(user, TestBackend.Password);
		DavServerOptions options = new() { BaseUrl = davUrl, HomeSetPath = TestBackend.DavHomeSetPath };

		using WebDavClient calClient = new(new Uri(davUrl), credentials);
		using WebDavClient cardClient = new(new Uri(davUrl), credentials);
		CalDavStore calendar = new(calClient, options, credentials, user, NullLogger.Instance);
		CardDavStore contacts = new(cardClient, options, credentials, NullLogger.Instance);

		foreach (IContentStore store in new IContentStore[] { calendar, contacts })
			try
			{
				foreach (BackendFolder folder in await store.ListFoldersAsync(ct))
				{
					IReadOnlyDictionary<string, string> revisions =
						await store.GetItemRevisionsAsync(folder.BackendKey, ContentFilter.All, ct);
					foreach (string href in revisions.Keys)
						try
						{
							await store.DeleteItemAsync(folder.BackendKey, href, false, ct);
						}
						catch
						{
							// best effort
						}
				}
			}
			catch
			{
				// DAV may be unavailable on this stack — cleanup is best effort
			}
	}

	public static async Task PurgeAllAsync(params string[] users)
	{
		foreach (string user in users)
		{
			await PurgeMailAsync(user);
			await PurgeDavAsync(user);
		}
	}
}
