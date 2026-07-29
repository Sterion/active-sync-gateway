using System.Text.Json;

namespace ActiveSync.Backends.Jmap;

// Free-text search (EAS Search command): one JMAP Email/query + Email/get pair, batched into a
// single request the same way GetItemRevisionsAsync's paging does.
public sealed partial class JmapMailStore
{
	public async Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
		string? folderBackendKey, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> filter = new() { ["text"] = freeText };
		if (folderBackendKey is not null)
			filter["inMailbox"] = FromKey(folderBackendKey);
		if (since is { } sinceValue)
			filter["after"] = JmapDate.ToUtc(sinceValue.UtcDateTime);

		JmapCall query = new("Email/query", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["filter"] = filter,
			["sort"] = new object[] { new Dictionary<string, object?> { ["property"] = "receivedAt", ["isAscending"] = false } },
			["limit"] = maxResults
		}, "0");
		JmapCall get = new("Email/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["#ids"] = ResultRef("0", "Email/query", "/ids"),
			["properties"] = new[] { "id", "mailboxIds" }
		}, "1");

		using JmapResponse response = await client.InvokeAsync(CapMail, [query, get], ct).ConfigureAwait(false);
		List<(string, string)> hits = new();
		foreach (JsonElement email in response.Arguments("1").GetProperty("list").EnumerateArray())
		{
			string id = email.GetProperty("id").GetString()!;
			string folderKey = folderBackendKey ?? FirstMailbox(email);
			if (folderKey.Length > 0)
				hits.Add((folderKey, id));
		}

		return hits;
	}

	private static string FirstMailbox(JsonElement email)
	{
		if (email.TryGetProperty("mailboxIds", out JsonElement ids) && ids.ValueKind == JsonValueKind.Object)
			foreach (JsonProperty p in ids.EnumerateObject())
				return ToKey(p.Name);
		return "";
	}
}
