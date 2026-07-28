using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Round 3, item 24 — IMAP correctness (<c>G3</c>, <c>G6</c>, <c>G7</c>, <c>G9</c>, <c>G12</c>,
///   <c>G13</c>, <c>G16</c>, <c>G22</c>). White-box: each test constructs
///   <see cref="ImapMailBackend" />/<see cref="ImapSession" /> directly against the real IMAP
///   backend (bypassing the EAS wire protocol) so it can drive the exact conditions each finding
///   describes, mirroring the direct-construction pattern already used for DAV findings (e.g.
///   <see cref="DavCreatePutReplayTests" />).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class ImapMailBackendCorrectnessTests
{
	private static ImapOptions Options => new()
	{
		Host = TestBackend.ImapHost,
		Port = TestBackend.ImapPort,
		UseSsl = false,
		Security = "None"
	};

	private static (ImapSession Session, ImapMailBackend Backend) CreateBackend(string user)
	{
		ImapSession session = new(Options, new BackendCredentials(user, TestBackend.Password), NullLogger.Instance);
		ImapMailBackend backend = new(session, user, _ => null, NullLogger.Instance);
		return (session, backend);
	}

	// G3 -----------------------------------------------------------------------------------

	/// <summary>
	///   G3: an unqualified (no "&lt;uidvalidity&gt;:" prefix) mail item key silently gets the
	///   folder's CURRENT UidValidity stamped onto it instead of being rejected, so a stale or
	///   hand-crafted key can address whatever message now holds that UID number. Proven against a
	///   real mailbox: append a message, then delete it by UID alone (no validity prefix) — the
	///   unqualified form must be refused, not resolved.
	/// </summary>
	[BackendFact]
	public async Task DeleteItemAsync_UnqualifiedItemKey_IsRejected_NotSilentlyResolved()
	{
		string user = TestBackend.User1;
		string folderName = $"ItemG3-{Guid.NewGuid():N}";
		CancellationToken ct = CancellationToken.None;

		uint uid;
		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			IMailFolder folder = await personal.CreateAsync(folderName, true, ct)
			                      ?? throw new InvalidOperationException("IMAP server did not return the created folder.");
			await folder.OpenAsync(FolderAccess.ReadWrite, ct);
			MimeMessage message = new();
			message.From.Add(MailboxAddress.Parse(user));
			message.To.Add(MailboxAddress.Parse(user));
			message.Subject = "G3";
			message.Body = new TextPart("plain") { Text = "g3-body" };
			UniqueId? appended = await folder.AppendAsync(message, MessageFlags.None, ct);
			Assert.NotNull(appended);
			uid = appended.Value.Id;
			await raw.DisconnectAsync(true, ct);
		}

		(ImapSession session, ImapMailBackend backend) = CreateBackend(user);
		try
		{
			string folderKey = ImapSession.ToBackendKey(folderName);
			// The client-echoed key carries NO "<uidvalidity>:" prefix — exactly the legacy/
			// hand-crafted shape G3 describes. Correct behavior: refused as not-found, never
			// silently resolved against the folder's current UidValidity.
			await Assert.ThrowsAsync<BackendItemNotFoundException>(
				() => backend.DeleteItemAsync(folderKey, uid.ToString(), true, ct));
		}
		finally
		{
			await session.DisposeAsync();
		}

		// The message must still exist — an unqualified key must never resolve to a real delete.
		using ImapClient verify = new();
		await verify.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
		await verify.AuthenticateAsync(user, TestBackend.Password, ct);
		IMailFolder verifyFolder = await verify.GetFolderAsync(folderName, ct);
		await verifyFolder.OpenAsync(FolderAccess.ReadOnly, ct);
		Assert.Equal(1, verifyFolder.Count);
		await verify.DisconnectAsync(true, ct);
	}
}
