using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;

namespace ActiveSync.Server.Eas.Handlers;

// Server → client item rendering: fetch a backend item and build its Add/Change element,
// including the 16.x draft marker and calendar-attachment FileReference qualification.
public sealed partial class SyncHandler
{
	private async Task<XElement?> BuildItemElementAsync(
		XName commandName, EasContext context, UserFolder folder, IContentStore store,
		string itemKey, BodyPreference bodyPreference, CancellationToken ct,
		IReadOnlyDictionary<string, string>? davIds = null,
		IReadOnlyDictionary<string, BackendItem?>? prefetched = null)
	{
		BackendItem? item;
		// F13: the window's items are fetched in one batched GetItemsAsync call up-front; use that
		// result when present. Fall back to a single fetch only when the batch didn't cover this key
		// (a store override that omitted a failed item) so a lone fetch failure still skips just that
		// item and re-tries next round rather than failing the whole collection.
		if (prefetched is not null && prefetched.TryGetValue(itemKey, out BackendItem? fetched))
		{
			item = fetched;
		}
		else
		{
			try
			{
				item = await store.GetItemAsync(folder.BackendKey, itemKey, bodyPreference, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogWarning(ex, "Fetching item {ItemKey} failed", itemKey);
				return null;
			}
		}

		if (item is null)
			return null;
		string serverId = await folders.ComposeServerIdAsync(folder, store, itemKey, ct, davIds);
		XElement applicationData = new(AS + "ApplicationData", item.ApplicationData);

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
