// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace ActiveSync.Contracts.Conformance;

/// <summary>
///   Runs a backend content store against the obligations the contract states in prose — the ones
///   a plugin author cannot discover from the type system, and which prose has repeatedly failed
///   to convey: that a revision is stable while an item is unchanged, that <c>null</c> from a batch
///   fetch means "not fetched" rather than "gone", that a created item is visible to the very next
///   enumeration, that a key space is the store's own, that <c>expected</c> is an upgrade and not
///   an obligation.
///   <para>
///     SCOPE: store obligations only. The engine-level invariants a plugin author might expect
///     here — SyncKey N-1 replay, windowing, echo suppression — belong to the gateway's sync
///     engine, not to a store: a store can neither pass nor fail them, and exercising them would
///     mean shipping that engine inside this package. They are covered by the gateway's own
///     in-repo suite instead. This is stated openly rather than promised and left unbuildable.
///   </para>
///   <para>
///     Framework-agnostic on purpose: the kit returns a <see cref="ConformanceReport" /> and
///     references nothing but <c>ActiveSync.Contracts</c>, so it runs from xunit, NUnit, MSTest or
///     a console app, and adds no test-framework dependency to a plugin that only wants the checks.
///   </para>
/// </summary>
public static class StoreConformance
{
	/// <summary>A key no store may claim — the foreign-key half of the key-space check.</summary>
	private const string ForeignFolderKey = "conformance-kit-foreign:/not-a-real-folder";

	/// <summary>An item key that must not exist, for the "null = not fetched" batch check.</summary>
	private const string MissingItemKey = "conformance-kit-missing:/not-a-real-item";

	/// <summary>Slack over the requested wait, so a loaded CI machine does not fail the timeout check.</summary>
	private static readonly TimeSpan WaitSlack = TimeSpan.FromSeconds(10);

	/// <summary>
	///   Runs every applicable check against one store. Never throws for a store's misbehaviour —
	///   a throwing store is a FAILED check, since the point is to report all of them at once.
	/// </summary>
	/// <param name="store">The store to exercise.</param>
	/// <param name="options">What the run may do, and where; defaults are documented on the type.</param>
	/// <param name="ct">Cancellation token; cancellation propagates rather than being reported.</param>
	/// <returns>The report — see <see cref="ConformanceReport.Failures" />.</returns>
	public static async Task<ConformanceReport> RunAsync(
		IContentStore store, ConformanceOptions? options = null, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		options ??= new ConformanceOptions();

		List<ConformanceCheck> checks = [];

		IReadOnlyList<BackendFolder> folders;
		try
		{
			folders = await store.ListFoldersAsync(ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			checks.Add(Record("folders.listed", ConformanceOutcome.Failed,
				$"ListFoldersAsync threw {ex.GetType().Name}: {ex.Message}"));
			return new ConformanceReport { Checks = checks };
		}

		checks.Add(CheckFolders(folders));
		checks.Add(CheckOwnership(store, folders));

		FolderKey? target = options.Folder ?? (folders.Count > 0 ? folders[0].Key : null);
		if (target is not { } folder)
		{
			checks.Add(Record("folder.target", ConformanceOutcome.Skipped,
				"the store listed no folder and none was configured, so no per-folder check ran"));
			return new ConformanceReport { Checks = checks };
		}

		await AddAsync(checks, "revisions.enumerated", () => CheckRevisionsAsync(store, folder, ct))
			.ConfigureAwait(false);
		await AddAsync(checks, "revisions.stable", () => CheckRevisionStabilityAsync(store, folder, ct))
			.ConfigureAwait(false);
		await AddAsync(checks, "wait.honours-timeout", () => CheckWaitAsync(store, folder, options, ct))
			.ConfigureAwait(false);

		if (!options.AllowMutation)
			checks.Add(Record("items.lifecycle", ConformanceOutcome.Skipped,
				"AllowMutation is false, so the create/update/delete checks did not run"));
		else
			await RunLifecycleAsync(store, folder, checks, ct).ConfigureAwait(false);

		return new ConformanceReport { Checks = checks };
	}

	// ---------- folder-level checks ----------

	private static ConformanceCheck CheckFolders(IReadOnlyList<BackendFolder> folders)
	{
		// A default-valued key carries a null Value despite the non-nullable annotation (every
		// struct has a default). The host never manufactures one, so a store returning one produces
		// a null-reference failure far from here.
		string[] problems =
		[
			.. folders
				.Where(f => string.IsNullOrEmpty(f.Key.Value))
				.Select(f => $"folder '{f.DisplayName}' has a default/empty key"),
			.. folders
				.Where(f => string.IsNullOrWhiteSpace(f.DisplayName))
				.Select(f => $"folder '{f.Key.Value}' has no display name"),
			.. folders
				.GroupBy(f => f.Key.Value, StringComparer.Ordinal)
				.Where(group => group.Count() > 1)
				.Select(group => $"folder key '{group.Key}' is listed {group.Count()} times")
		];

		return problems.Length > 0
			? Record("folders.listed", ConformanceOutcome.Failed, string.Join("; ", problems))
			: Record("folders.listed", ConformanceOutcome.Passed,
				$"{folders.Count} folder(s), each with a distinct non-empty key and a display name");
	}

	private static ConformanceCheck CheckOwnership(IContentStore store, IReadOnlyList<BackendFolder> folders)
	{
		try
		{
			string[] disowned =
			[
				.. folders.Where(f => !store.OwnsKey(f.Key)).Select(f => f.Key.Value)
			];
			if (disowned.Length > 0)
				return Record("folders.owned", ConformanceOutcome.Failed,
					"OwnsKey rejected the store's own folder key(s): " + string.Join(", ", disowned));

			return store.OwnsKey(new FolderKey(ForeignFolderKey))
				? Record("folders.owned", ConformanceOutcome.Failed,
					"OwnsKey claimed a foreign key, so this store's key space is not disjoint from another's")
				: Record("folders.owned", ConformanceOutcome.Passed,
					"OwnsKey claims every listed folder and rejects a foreign key");
		}
		catch (Exception ex)
		{
			return Record("folders.owned", ConformanceOutcome.Failed,
				$"OwnsKey threw {ex.GetType().Name}: {ex.Message} — it must answer for any key, including another store's");
		}
	}

	// ---------- revision-map checks ----------

	private static async Task<ConformanceCheck> CheckRevisionsAsync(
		IContentStore store, FolderKey folder, CancellationToken ct)
	{
		IReadOnlyDictionary<ItemKey, ItemRevision> revisions =
			await store.GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);

		string[] problems =
		[
			.. revisions
				.Where(pair => string.IsNullOrEmpty(pair.Key.Value))
				.Select(_ => "an item key is default/empty"),
			.. revisions
				.Where(pair => string.IsNullOrEmpty(pair.Value.Value))
				.Select(pair => $"item '{pair.Key.Value}' has a default/empty revision")
		];

		return problems.Length > 0
			? Failed("revisions.enumerated", string.Join("; ", problems))
			: Passed("revisions.enumerated",
				$"{revisions.Count} item(s), each with a non-empty key and revision");
	}

	private static async Task<ConformanceCheck> CheckRevisionStabilityAsync(
		IContentStore store, FolderKey folder, CancellationToken ct)
	{
		IReadOnlyDictionary<ItemKey, ItemRevision> first =
			await store.GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);
		IReadOnlyDictionary<ItemKey, ItemRevision> second =
			await store.GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);

		// Items may legitimately arrive or vanish between the two enumerations (another client, a
		// delivery). Only the items present BOTH times prove anything about revision stability.
		string[] unstable =
		[
			.. first
				.Where(pair => second.TryGetValue(pair.Key, out ItemRevision now) && now != pair.Value)
				.Select(pair => $"'{pair.Key.Value}' changed revision without changing")
		];

		return unstable.Length > 0
			? Failed("revisions.stable",
				string.Join("; ", unstable) +
				" — a revision that moves on its own re-sends the item to every device on every sync")
			: Passed("revisions.stable",
				$"{first.Count} item revision(s) unchanged across two consecutive enumerations");
	}

	private static async Task<ConformanceCheck> CheckWaitAsync(
		IContentStore store, FolderKey folder, ConformanceOptions options, CancellationToken ct)
	{
		Stopwatch elapsed = Stopwatch.StartNew();
		IReadOnlyList<FolderKey> changed =
			await store.WaitForChangesAsync([folder], options.WaitTimeout, ct).ConfigureAwait(false);
		elapsed.Stop();

		if (elapsed.Elapsed > options.WaitTimeout + WaitSlack)
			return Failed("wait.honours-timeout",
				$"WaitForChangesAsync took {elapsed.Elapsed} for a {options.WaitTimeout} timeout — a wait that " +
				"overruns holds a client's long-poll open past its heartbeat");

		string[] foreign = [.. changed.Where(key => key != folder).Select(key => key.Value)];
		return foreign.Length > 0
			? Failed("wait.honours-timeout",
				"WaitForChangesAsync reported folder(s) it was not asked to watch: " + string.Join(", ", foreign))
			: Passed("wait.honours-timeout",
				$"returned {changed.Count} changed folder(s) after {elapsed.Elapsed.TotalSeconds:F1}s, within the timeout");
	}

	// ---------- item lifecycle ----------

	private static Task RunLifecycleAsync(
		IContentStore store, FolderKey folder, List<ConformanceCheck> checks, CancellationToken ct)
	{
		string uid = "conformance-" + Guid.NewGuid().ToString("N");

		switch (store)
		{
			case IContentStore<CalendarItem> calendar:
				return RunLifecycleAsync(calendar, folder, checks,
					new CalendarItem { ICalendar = SamplePayloads.Event(uid, "Conformance") },
					new CalendarItem { ICalendar = SamplePayloads.Event(uid, "Conformance (edited)") },
					item => item.ICalendar, uid, ct);

			case IContentStore<TaskItem> tasks:
				return RunLifecycleAsync(tasks, folder, checks,
					new TaskItem { ICalendar = SamplePayloads.Task(uid, "Conformance") },
					new TaskItem { ICalendar = SamplePayloads.Task(uid, "Conformance (edited)") },
					item => item.ICalendar, uid, ct);

			case IContentStore<ContactItem> contacts:
				return RunLifecycleAsync(contacts, folder, checks,
					new ContactItem { VCard = SamplePayloads.Contact(uid, "Conformance") },
					new ContactItem { VCard = SamplePayloads.Contact(uid, "Conformance Edited") },
					item => item.VCard, uid, ct);

			case IContentStore<NoteItem> notes:
				return RunLifecycleAsync(notes, folder, checks,
					SamplePayloads.Note(uid, "Conformance"),
					SamplePayloads.Note(uid, "Conformance (edited)"),
					item => item.Subject + "\n" + item.Body.Content, uid, ct);

			case IMailStore:
				checks.Add(Record("items.lifecycle", ConformanceOutcome.Skipped,
					"mail: the only content create a client can make is a Drafts-folder draft, which the " +
					"kit will not synthesize into a folder it cannot know is Drafts"));
				return Task.CompletedTask;

			default:
				checks.Add(Record("items.lifecycle", ConformanceOutcome.Skipped,
					$"{store.GetType().Name} implements no content-class alias the kit carries a sample payload for"));
				return Task.CompletedTask;
		}
	}

	private static async Task RunLifecycleAsync<TItem>(
		IContentStore<TItem> store,
		FolderKey folder,
		List<ConformanceCheck> checks,
		TItem created,
		TItem updated,
		Func<TItem, string> identity,
		string uid,
		CancellationToken ct) where TItem : class
	{
		(ItemKey Key, ItemRevision Revision) item;
		try
		{
			item = await store.CreateItemAsync(folder, created, ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			checks.Add(Record("item.create", ConformanceOutcome.Failed,
				$"CreateItemAsync threw {ex.GetType().Name}: {ex.Message}"));
			return;
		}

		if (string.IsNullOrEmpty(item.Key.Value) || string.IsNullOrEmpty(item.Revision.Value))
		{
			checks.Add(Record("item.create", ConformanceOutcome.Failed,
				"CreateItemAsync returned a default/empty key or revision"));
			return;
		}

		checks.Add(Record("item.create", ConformanceOutcome.Passed,
			$"created '{item.Key.Value}' at revision '{item.Revision.Value}'"));

		try
		{
			await AddAsync(checks, "item.create-is-visible",
					() => CheckVisibleAsync(store, folder, item, ct))
				.ConfigureAwait(false);
			await AddAsync(checks, "item.round-trips",
					() => CheckRoundTripAsync(store, folder, item.Key, identity, uid, ct))
				.ConfigureAwait(false);
			await AddAsync(checks, "items.batch-null-is-not-fetched",
					() => CheckBatchAsync(store, folder, item.Key, ct))
				.ConfigureAwait(false);
			await AddAsync(checks, "item.update",
					() => CheckUpdateAsync(store, folder, item.Key, updated, ct))
				.ConfigureAwait(false);
			await AddAsync(checks, "item.update-precondition",
					() => CheckPreconditionAsync(store, folder, item.Key, updated, ct))
				.ConfigureAwait(false);
		}
		finally
		{
			await AddAsync(checks, "item.delete", () => CheckDeleteAsync(store, folder, item.Key, ct))
				.ConfigureAwait(false);
		}
	}

	private static async Task<ConformanceCheck> CheckVisibleAsync<TItem>(
		IContentStore<TItem> store, FolderKey folder, (ItemKey Key, ItemRevision Revision) item,
		CancellationToken ct) where TItem : class
	{
		IReadOnlyDictionary<ItemKey, ItemRevision> revisions =
			await store.GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);

		if (!revisions.TryGetValue(item.Key, out ItemRevision listed))
			return Failed("item.create-is-visible",
				$"'{item.Key.Value}' was created but the next enumeration does not list it — the engine " +
				"enumerates the whole collection every round, so an item it cannot see does not exist");

		return listed == item.Revision
			? Passed("item.create-is-visible", "the created item is listed at the revision create returned")
			: Failed("item.create-is-visible",
				$"create returned revision '{item.Revision.Value}' but the enumeration reports " +
				$"'{listed.Value}' — the client would be sent the item again immediately");
	}

	private static async Task<ConformanceCheck> CheckRoundTripAsync<TItem>(
		IContentStore<TItem> store, FolderKey folder, ItemKey key, Func<TItem, string> identity,
		string uid, CancellationToken ct) where TItem : class
	{
		TItem? fetched = await store.GetItemAsync(folder, key, ct).ConfigureAwait(false);
		if (fetched is null)
			return Failed("item.round-trips", "GetItemAsync returned null for an item that exists");

		// Deliberately not byte equality: a backend may reformat, reorder or add properties of its
		// own (a DAV server stamping its own timestamps is normal and correct). What must survive is
		// the identity the payload was given, because that is what every later read is matched on.
		return identity(fetched).Contains(uid, StringComparison.Ordinal)
			? Passed("item.round-trips", "the fetched payload still carries the identity it was created with")
			: Failed("item.round-trips",
				$"the fetched payload no longer carries '{uid}' — the store rewrote the payload it was given");
	}

	private static async Task<ConformanceCheck> CheckBatchAsync<TItem>(
		IContentStore<TItem> store, FolderKey folder, ItemKey key, CancellationToken ct)
		where TItem : class
	{
		ItemKey missing = new(MissingItemKey);
		IReadOnlyDictionary<ItemKey, TItem?> fetched =
			await store.GetItemsAsync(folder, [key, missing], ct).ConfigureAwait(false);

		if (!fetched.TryGetValue(key, out TItem? real) || real is null)
			return Failed("items.batch-null-is-not-fetched",
				"the batch fetch returned null for an item that exists — null means \"not fetched\", so the " +
				"engine would skip it and retry it forever");

		return fetched.TryGetValue(missing, out TItem? gone) && gone is not null
			? Failed("items.batch-null-is-not-fetched", "the batch fetch returned a payload for a key that does not exist")
			: Passed("items.batch-null-is-not-fetched",
				"the batch fetch returned the real item and absent-or-null for the missing one, without throwing");
	}

	private static async Task<ConformanceCheck> CheckUpdateAsync<TItem>(
		IContentStore<TItem> store, FolderKey folder, ItemKey key, TItem updated, CancellationToken ct)
		where TItem : class
	{
		ItemRevision revision = await store.UpdateItemAsync(folder, key, updated, null, ct)
			.ConfigureAwait(false);
		if (string.IsNullOrEmpty(revision.Value))
			return Failed("item.update", "UpdateItemAsync returned a default/empty revision");

		IReadOnlyDictionary<ItemKey, ItemRevision> revisions =
			await store.GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);

		if (!revisions.TryGetValue(key, out ItemRevision listed))
			return Failed("item.update", $"'{key.Value}' vanished from the enumeration after an update");

		return listed == revision
			? Passed("item.update", "the revision update returned is the one the enumeration reports")
			: Failed("item.update",
				$"update returned '{revision.Value}' but the enumeration reports '{listed.Value}' — the " +
				"engine writes the returned revision into its snapshot, so the item would be re-sent");
	}

	private static async Task<ConformanceCheck> CheckPreconditionAsync<TItem>(
		IContentStore<TItem> store, FolderKey folder, ItemKey key, TItem value, CancellationToken ct)
		where TItem : class
	{
		ItemRevision stale = new("conformance-kit-stale-revision");
		try
		{
			await store.UpdateItemAsync(folder, key, value, stale, ct).ConfigureAwait(false);
		}
		catch (BackendPreconditionFailedException)
		{
			return Passed("item.update-precondition",
				"a stale expected revision was refused with BackendPreconditionFailedException");
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return Failed("item.update-precondition",
				$"a stale expected revision produced {ex.GetType().Name}: {ex.Message} — a store that checks the " +
				"precondition must signal it with BackendPreconditionFailedException so the host can re-merge " +
				"and retry, and a store that cannot check it must ignore `expected` instead of failing");
		}

		return Skipped("item.update-precondition",
			"the store ignores `expected` and applied the write — conforming: the precondition is an upgrade " +
			"for stores that can honour it (DAV If-Match, JMAP ifInState), never an obligation");
	}

	private static async Task<ConformanceCheck> CheckDeleteAsync<TItem>(
		IContentStore<TItem> store, FolderKey folder, ItemKey key, CancellationToken ct)
		where TItem : class
	{
		await store.DeleteItemAsync(folder, key, true, ct).ConfigureAwait(false);

		IReadOnlyDictionary<ItemKey, ItemRevision> revisions =
			await store.GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);
		if (revisions.ContainsKey(key))
			return Failed("item.delete", $"'{key.Value}' is still enumerated after a permanent delete");

		try
		{
			// The contract says a fetch of a gone item returns null; a store that raises the typed
			// item-gone error instead conveys the same fact and the host funnels it the same way.
			TItem? fetched = await store.GetItemAsync(folder, key, ct).ConfigureAwait(false);
			return fetched is null
				? Passed("item.delete", "the deleted item is gone from the enumeration and fetches as null")
				: Failed("item.delete", "GetItemAsync still returns a payload for a deleted item");
		}
		catch (BackendItemNotFoundException)
		{
			return Passed("item.delete",
				"the deleted item is gone from the enumeration and fetches as BackendItemNotFoundException");
		}
	}

	// ---------- plumbing ----------

	private static async Task AddAsync(
		List<ConformanceCheck> checks, string name, Func<Task<ConformanceCheck>> body)
	{
		try
		{
			checks.Add(await body().ConfigureAwait(false));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// One check's failure must never hide the rest — the report is the deliverable.
			checks.Add(Record(name, ConformanceOutcome.Failed, $"threw {ex.GetType().Name}: {ex.Message}"));
		}
	}

	private static ConformanceCheck Passed(string name, string detail) =>
		Record(name, ConformanceOutcome.Passed, detail);

	private static ConformanceCheck Failed(string name, string detail) =>
		Record(name, ConformanceOutcome.Failed, detail);

	private static ConformanceCheck Skipped(string name, string detail) =>
		Record(name, ConformanceOutcome.Skipped, detail);

	private static ConformanceCheck Record(string name, ConformanceOutcome outcome, string detail) =>
		new() { Name = name, Outcome = outcome, Detail = detail };
}
