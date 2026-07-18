using System.Text.Json;
using ActiveSync.Core.Backend;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Out-of-office over JMAP (RFC 8621 §8 VacationResponse) — a per-account singleton, so
///   there is no separate script to restore: Enable arms it from the semantic reply, Disable
///   turns it off. The gateway's Oof state database remains the source of truth, so the
///   restore token is unused (empty).
/// </summary>
public sealed class JmapOofBackend(JmapClient client) : IOofBackend
{
	private static readonly string[] Cap = [JmapCapabilities.Core, JmapCapabilities.VacationResponse];

	private string? _account;

	public async Task<string?> EnableAsync(OofReply reply, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> patch = new()
		{
			["isEnabled"] = true,
			["textBody"] = reply.BodyIsHtml ? null : reply.BodyText,
			["htmlBody"] = reply.BodyIsHtml ? reply.BodyText : null,
			["fromDate"] = Utc(reply.StartUtc),
			["toDate"] = Utc(reply.EndUtc)
		};
		await SetSingletonAsync(account, patch, ct).ConfigureAwait(false);
		return ""; // singleton — nothing to restore beyond "disabled"
	}

	public async Task DisableAsync(string restoreToken, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		await SetSingletonAsync(account, new Dictionary<string, object?> { ["isEnabled"] = false }, ct)
			.ConfigureAwait(false);
	}

	private async Task SetSingletonAsync(string account, Dictionary<string, object?> patch, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(Cap, "VacationResponse/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?> { ["singleton"] = patch }
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		if (args.TryGetProperty("notUpdated", out JsonElement notUpdated) &&
		    notUpdated.ValueKind == JsonValueKind.Object &&
		    notUpdated.TryGetProperty("singleton", out JsonElement error))
		{
			string type = error.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
			throw new BackendException($"JMAP VacationResponse/set failed: {type}.");
		}
	}

	private async Task<string> AccountAsync(CancellationToken ct)
	{
		return _account ??= (await client.GetSessionAsync(ct).ConfigureAwait(false))
			.PrimaryAccount(JmapCapabilities.VacationResponse);
	}

	private static string? Utc(DateTime? value)
	{
		return value is { } v ? JmapDate.ToUtc(v) : null;
	}
}
