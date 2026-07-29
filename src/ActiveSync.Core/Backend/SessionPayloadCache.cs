using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Revision-keyed payload cache hung off one composite session: the raw iCalendar/vCard text
///   the gateway last SENT to this (user, device), keyed by folder + item and valid only for the
///   revision it was sent at. A client can only edit an item it was sent, so a partial-update
///   merge arriving against the cached revision needs NO backend fetch at all; a miss — or a
///   revision that moved on — falls back to a fetch, which is exactly what correctness demands.
/// </summary>
/// <remarks>
///   Deliberately per-session, never global: these are decrypted user payloads (for local
///   stores, literally the plaintext of sealed database rows) — a cross-user cache would be a
///   disclosure surface for no benefit. Mail never needs an entry (its Change is a flags patch
///   outside the RFC822) and notes merge against the store's typed row directly, so what this
///   holds is iCalendar and vCard strings: kilobytes. The count bound keeps the worst case
///   trivial, evicting least-recently-used first, and the session's existing idle eviction is
///   the lifetime — no new machinery.
/// </remarks>
public sealed class SessionPayloadCache
{
	private const int Capacity = 256;

	private readonly object _gate = new();
	private readonly Dictionary<(FolderKey Folder, ItemKey Item), Entry> _entries = new();
	private readonly LinkedList<(FolderKey Folder, ItemKey Item)> _order = new();

	private sealed class Entry
	{
		public required ItemRevision Revision { get; set; }
		public required string Payload { get; set; }
		public required LinkedListNode<(FolderKey Folder, ItemKey Item)> Node { get; init; }
	}

	/// <summary>
	///   The payload last sent for the item — only when it was sent at exactly
	///   <paramref name="revision" />. A different revision is a miss (the item moved on
	///   underneath; the caller must fetch fresh).
	/// </summary>
	/// <param name="folder">The folder holding the item.</param>
	/// <param name="item">The item.</param>
	/// <param name="revision">The revision the caller believes the client acknowledged.</param>
	/// <param name="payload">The cached payload text on a hit.</param>
	/// <returns><c>true</c> on a revision-exact hit.</returns>
	public bool TryGet(FolderKey folder, ItemKey item, ItemRevision revision, out string payload)
	{
		lock (_gate)
		{
			if (_entries.TryGetValue((folder, item), out Entry? entry) && entry.Revision == revision)
			{
				_order.Remove(entry.Node);
				_order.AddLast(entry.Node);
				payload = entry.Payload;
				return true;
			}
		}

		payload = string.Empty;
		return false;
	}

	/// <summary>Records the payload as sent at the given revision, evicting LRU entries over the bound.</summary>
	/// <param name="folder">The folder holding the item.</param>
	/// <param name="item">The item.</param>
	/// <param name="revision">The revision the payload was sent at.</param>
	/// <param name="payload">The payload text as handed over by the store.</param>
	public void Set(FolderKey folder, ItemKey item, ItemRevision revision, string payload)
	{
		lock (_gate)
		{
			if (_entries.TryGetValue((folder, item), out Entry? existing))
			{
				existing.Revision = revision;
				existing.Payload = payload;
				_order.Remove(existing.Node);
				_order.AddLast(existing.Node);
				return;
			}

			LinkedListNode<(FolderKey, ItemKey)> node = _order.AddLast((folder, item));
			_entries[(folder, item)] = new Entry { Revision = revision, Payload = payload, Node = node };

			while (_entries.Count > Capacity && _order.First is { } oldest)
			{
				_order.RemoveFirst();
				_entries.Remove(oldest.Value);
			}
		}
	}

	/// <summary>
	///   Drops the item's entry (a failed precondition proved it stale; a delete made it moot).
	/// </summary>
	/// <param name="folder">The folder holding the item.</param>
	/// <param name="item">The item whose entry to drop.</param>
	public void Remove(FolderKey folder, ItemKey item)
	{
		lock (_gate)
		{
			if (_entries.Remove((folder, item), out Entry? entry))
				_order.Remove(entry.Node);
		}
	}
}
