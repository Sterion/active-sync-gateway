using System.Xml.Linq;
using ActiveSync.Core.State;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol;
using ActiveSync.Server.Eas.Content;

namespace ActiveSync.Server.Eas.Handlers;

// Server → client item rendering: fetch a backend item's typed payload, convert it to EAS
// ApplicationData host-side, and build its Add/Change element — including the 16.x draft marker
// and calendar-attachment FileReference qualification.
public sealed partial class SyncHandler
{
	private async Task<XElement?> BuildItemElementAsync(
		XName commandName, EasContext context, UserFolder folder, ContentAdapter store,
		string itemKey, BodyPreference bodyPreference, CancellationToken ct,
		IReadOnlyDictionary<string, string>? davIds = null,
		IReadOnlyDictionary<string, object?>? prefetched = null,
		string? revisionForCache = null)
	{
		object? item;
		// The window's items are fetched in one batched GetItemsAsync call up-front; use that
		// result when present. Fall back to a single fetch only when the batch didn't cover this key
		// (a store override that omitted a failed item) so a lone fetch failure still skips just that
		// item and re-tries next round rather than failing the whole collection.
		if (prefetched is not null && prefetched.TryGetValue(itemKey, out object? fetched))
		{
			item = fetched;
		}
		else
		{
			try
			{
				item = await store.GetItemAsync(folder.BackendKey, itemKey, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogWarning(ex, "Fetching item {ItemKey} failed", itemKey);
				return null;
			}
		}

		if (item is null)
			return null;
		List<XElement>? rendered = store.Render(item, bodyPreference, folder.BackendKey, itemKey);
		// An unrenderable (malformed/oversized) payload behaves exactly as a fetch failure —
		// skipped, snapshot not advanced, retried next round (the contract's defensive rule).
		if (rendered is null)
		{
			logger.LogWarning("Item {ItemKey} in \"{Folder}\" could not be converted; skipping it this round",
				itemKey, folder.DisplayName);
			return null;
		}

		// The payload the client is about to receive is the basis its next partial update merges
		// onto — cache it at the revision this round is delivering (payload-text classes only).
		if (revisionForCache is not null)
			store.CacheRendered(folder.BackendKey, itemKey, revisionForCache, item);

		string serverId = await folders.ComposeServerIdAsync(folder, itemKey, ct, davIds);
		XElement applicationData = new(AS + "ApplicationData", rendered);

		// 16.x drafts: items in the Drafts folder carry email2:IsDraft so the client opens
		// them in the composer instead of the reader.
		if (bodyPreference.Eas16 && folder.Type == EasFolderType.Drafts &&
		    store.EasClass.Equals(EasClass.Email, StringComparison.OrdinalIgnoreCase) &&
		    applicationData.Element(E2 + "IsDraft") is null)
			applicationData.Add(new XElement(E2 + "IsDraft", "1"));

		QualifyCalendarAttachmentReferences(applicationData, serverId);

		return new XElement(commandName,
			new XElement(AS + "ServerId", serverId),
			applicationData);
	}

	/// <summary>
	///   The calendar converter emits attachment FileReferences as "calatt::&lt;index&gt;"
	///   because it cannot know item identity; the full ItemOperations-resolvable shape is
	///   "calatt::&lt;serverId&gt;::&lt;index&gt;", stamped here where the ServerId exists.
	/// </summary>
	private static void QualifyCalendarAttachmentReferences(XElement applicationData, string serverId)
	{
		const string prefix = "calatt::";
		foreach (XElement reference in applicationData.Descendants(ASB + "FileReference"))
		{
			if (!reference.Value.StartsWith(prefix, StringComparison.Ordinal))
				continue;
			string tail = reference.Value[prefix.Length..];
			if (!tail.Contains("::", StringComparison.Ordinal))
				reference.Value = prefix + serverId + "::" + tail;
		}
	}
}
