using System.Text.Json;
using ActiveSync.Contracts;

namespace ActiveSync.Backends.Jmap;

// Free-text search (EAS Search command): one JMAP Email/query + Email/get pair, batched into a
// single request the same way GetItemRevisionsAsync's paging does.
public sealed partial class JmapMailStore
{
	/// <inheritdoc />
	public async Task<IReadOnlyList<SearchHit>> SearchAsync(
		FolderKey? folder, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> filter = new() { ["text"] = freeText };
		if (folder is { } folderKey)
			filter["inMailbox"] = FromKey(folderKey.Value);
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
		List<SearchHit> hits = new();
		foreach (JsonElement email in response.Arguments("1").GetProperty("list").EnumerateArray())
		{
			string id = email.GetProperty("id").GetString()!;
			string hitFolder = folder?.Value ?? FirstMailbox(email);
			if (hitFolder.Length > 0)
				hits.Add(new SearchHit { Folder = new FolderKey(hitFolder), Item = new ItemKey(id) });
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
