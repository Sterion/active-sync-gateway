using System.Text.Json;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Outbound mail submission over JMAP (RFC 8621 EmailSubmission). The raw MIME is uploaded
///   as a blob and imported as a temporary Email, then EmailSubmission/set delivers it and
///   destroys the temporary copy on success (<c>onSuccessDestroyEmail</c>) so nothing lingers
///   in Drafts. Bcc recipients are moved from the header into the envelope so they are
///   delivered but not disclosed in the sent message — the same behavior MailKit gives SMTP.
/// </summary>
public sealed class JmapMailSubmit(
	JmapClient client,
	string? mailAddress,
	ILogger logger) : IMailSubmitOperations
{
	private static readonly string[] Cap =
		[JmapCapabilities.Core, JmapCapabilities.Mail, JmapCapabilities.Submission];

	private string? _account;

	public async Task SendAsync(byte[] mime, CancellationToken ct)
	{
		string accountId = await AccountAsync(ct).ConfigureAwait(false);
		using MemoryStream input = new(mime);
		MimeMessage message = await MimeMessage.LoadAsync(input, ct).ConfigureAwait(false);

		string from = message.From.Mailboxes.FirstOrDefault()?.Address ?? mailAddress
			?? throw new BackendException("Outgoing message has no From address.");
		List<string> recipients = message.To.Mailboxes
			.Concat(message.Cc.Mailboxes)
			.Concat(message.Bcc.Mailboxes)
			.Select(m => m.Address)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (recipients.Count == 0)
			throw new BackendException("Outgoing message has no recipients.");

		// Strip Bcc from the delivered bytes; the envelope below still delivers to them.
		message.Bcc.Clear();
		using MemoryStream output = new();
		await message.WriteToAsync(output, ct).ConfigureAwait(false);
		string blobId = await client.UploadBlobAsync(accountId, output.ToArray(), "message/rfc822", ct)
			.ConfigureAwait(false);

		(string identityId, string draftsMailboxId) =
			await ResolvePrerequisitesAsync(accountId, from, ct).ConfigureAwait(false);

		Dictionary<string, object?> envelope = new()
		{
			["mailFrom"] = new Dictionary<string, object?> { ["email"] = from },
			["rcptTo"] = recipients.Select(r => new Dictionary<string, object?> { ["email"] = r }).ToArray()
		};

		JmapCall import = new("Email/import", new Dictionary<string, object?>
		{
			["accountId"] = accountId,
			["emails"] = new Dictionary<string, object?>
			{
				["m"] = new Dictionary<string, object?>
				{
					["blobId"] = blobId,
					["mailboxIds"] = new Dictionary<string, object?> { [draftsMailboxId] = true },
					["keywords"] = new Dictionary<string, object?> { ["$draft"] = true }
				}
			}
		}, "0");
		JmapCall submit = new("EmailSubmission/set", new Dictionary<string, object?>
		{
			["accountId"] = accountId,
			["onSuccessDestroyEmail"] = new[] { "#s" },
			["create"] = new Dictionary<string, object?>
			{
				["s"] = new Dictionary<string, object?>
				{
					["identityId"] = identityId,
					// JMAP creation reference (RFC 8620 §5.3): the Email created with creation
					// id "m" by the Email/import call above, referenced by "#m".
					["emailId"] = "#m",
					["envelope"] = envelope
				}
			}
		}, "1");

		using JmapResponse response = await client.InvokeAsync(Cap, [import, submit], ct).ConfigureAwait(false);
		JsonElement result = response.Arguments("1");
		if (result.TryGetProperty("notCreated", out JsonElement notCreated) &&
		    notCreated.ValueKind == JsonValueKind.Object &&
		    notCreated.TryGetProperty("s", out JsonElement error))
		{
			string type = error.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
			throw new BackendException($"JMAP EmailSubmission failed: {type}.");
		}

		logger.LogInformation("Submitted message via JMAP for {From}", from);
	}

	private async Task<string> AccountAsync(CancellationToken ct)
	{
		return _account ??= (await client.GetSessionAsync(ct).ConfigureAwait(false))
			.PrimaryAccount(JmapCapabilities.Mail);
	}

	/// <summary>Fetches the sending identity (matching the From address if possible) and the Drafts mailbox.</summary>
	private async Task<(string IdentityId, string DraftsMailboxId)> ResolvePrerequisitesAsync(
		string accountId, string from, CancellationToken ct)
	{
		JmapCall identities = new("Identity/get", new Dictionary<string, object?>
		{
			["accountId"] = accountId,
			["ids"] = null
		}, "0");
		JmapCall mailboxes = new("Mailbox/get", new Dictionary<string, object?>
		{
			["accountId"] = accountId,
			["ids"] = null,
			["properties"] = new[] { "id", "role" }
		}, "1");

		using JmapResponse response = await client.InvokeAsync(Cap, [identities, mailboxes], ct).ConfigureAwait(false);

		string? identityId = null;
		string? fallbackIdentity = null;
		foreach (JsonElement identity in response.Arguments("0").GetProperty("list").EnumerateArray())
		{
			string id = identity.GetProperty("id").GetString()!;
			fallbackIdentity ??= id;
			if (identity.TryGetProperty("email", out JsonElement email) &&
			    string.Equals(email.GetString(), from, StringComparison.OrdinalIgnoreCase))
			{
				identityId = id;
				break;
			}
		}

		identityId ??= fallbackIdentity
			?? throw new BackendException("JMAP server exposes no sending identity for this account.");

		string? draftsId = null;
		string? fallbackMailbox = null;
		foreach (JsonElement mailbox in response.Arguments("1").GetProperty("list").EnumerateArray())
		{
			string id = mailbox.GetProperty("id").GetString()!;
			fallbackMailbox ??= id;
			if (mailbox.TryGetProperty("role", out JsonElement role) && role.GetString() == "drafts")
			{
				draftsId = id;
				break;
			}
		}

		return (identityId, draftsId ?? fallbackMailbox
			?? throw new BackendException("JMAP account has no mailbox to stage the outgoing message in."));
	}
}
