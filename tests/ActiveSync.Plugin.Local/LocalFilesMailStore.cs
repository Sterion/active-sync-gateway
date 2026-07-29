using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   The mail class over <c>&lt;root&gt;/mail</c>: one <c>.eml</c> file per message, holding the
///   raw RFC822 exactly as it arrived, with the flags and categories carried in the file NAME (see
///   <see cref="MailFileName" />).
/// </summary>
/// <remarks>
///   <para>
///     <see cref="IMailboxOperations" /> is implemented alongside <see cref="IMailStore" /> because
///     the host REQUIRES it of whatever fills the MailStore role — a mail store without it fails
///     the session build outright. <see cref="IItemMoveOperations" /> makes MoveItems work (a file
///     move) and <see cref="IFolderOperations" /> makes client folder create/delete work
///     (a directory).
///   </para>
///   <para>
///     Files whose names this store does not recognise are ADOPTED on the next enumeration: they
///     are renamed once to a minted key. That is the price of carrying metadata in the name with
///     no sidecar file, and it is what lets someone drop an <c>.eml</c> into
///     <c>mail/Inbox</c> and watch it arrive on the phone.
///   </para>
/// </remarks>
internal sealed class LocalFilesMailStore(
	LocalFilesOptions options, MailFolderTree tree, FileTreeWatcher watcher)
	: IMailStore, IMailboxOperations, IItemMoveOperations, IFolderOperations
{
	private readonly Lock _indexGate = new();
	private readonly Dictionary<string, FolderIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public bool OwnsKey(FolderKey key)
	{
		return tree.Owns(key);
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		tree.EnsureSpecialFolders();
		return Task.FromResult(tree.List());
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		Dictionary<ItemKey, ItemRevision> revisions = [];
		if (!directory.Exists)
			return revisions;

		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		foreach ((string key, string fileName) in index.Files)
		{
			ct.ThrowIfCancellationRequested();
			string path = Path.Combine(directory.FullName, fileName);
			// A persistent stat failure throws out of here rather than dropping the entry: the map
			// is the WHOLE truth for the folder, so a silently missing item is a delete pushed to
			// every device that holds it.
			(long Length, long WriteTicks)? stat = await AtomicFile.StatAsync(path, ct).ConfigureAwait(false);
			if (stat is null)
				continue;
			if (filter.Since is { } since && new DateTime(stat.Value.WriteTicks, DateTimeKind.Utc) < since.UtcDateTime)
				// The host asked for a date window (FilterType); older messages may be omitted, and
				// it reconciles the resulting aged-out deletes itself.
				continue;
			if (!MailFileName.TryParse(fileName, out MailFileName parsed))
				continue;

			revisions[new ItemKey(key)] = new ItemRevision(
				MailFileName.RevisionOf(parsed.Flags, parsed.Categories, stat.Value.Length, stat.Value.WriteTicks));
		}

		return revisions;
	}

	/// <inheritdoc />
	public async Task<MailItem?> GetItemAsync(
		FolderKey folder, ItemKey item, MailFetchOptions fetchOptions, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		return await ReadAsync(directory, index, item, ct).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<ItemKey, MailItem?>> GetItemsAsync(
		FolderKey folder, IReadOnlyList<ItemKey> items, MailFetchOptions fetchOptions, CancellationToken ct)
	{
		// Overridden rather than inherited: the default loops GetItemAsync, and each of those calls
		// would re-scan the directory to resolve a key to its file name.
		DirectoryInfo directory = tree.Resolve(folder);
		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		Dictionary<ItemKey, MailItem?> fetched = new(items.Count);
		foreach (ItemKey item in items)
			try
			{
				fetched[item] = await ReadAsync(directory, index, item, ct).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// "Not fetched", so the host retries it next round instead of dropping it.
				fetched[item] = null;
			}

		return fetched;
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
		FolderKey folder, MailItem item, CancellationToken ct)
	{
		RequireDrafts(folder, "create a draft in");
		DirectoryInfo directory = tree.Resolve(folder);
		directory.Create();

		MailFlags flags = item.Flags with { Draft = true };
		string key = ItemKeyMint.Mint();
		string fileName = MailFileName.Compose(key, flags, item.Categories, out IReadOnlyList<string> stored);
		string path = Path.Combine(directory.FullName, fileName);
		await AtomicFile.WriteAsync(path, item.Rfc822, ct).ConfigureAwait(false);
		Invalidate(directory);
		watcher.NotifyChanged(directory.FullName);
		return (new ItemKey(key), await RevisionAsync(path, flags, stored, ct).ConfigureAwait(false));
	}

	/// <inheritdoc />
	public async Task<ItemRevision> UpdateFlagsAsync(
		FolderKey folder, ItemKey item, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		if (!index.Files.TryGetValue(item.Value, out string? fileName)
		    || !MailFileName.TryParse(fileName, out MailFileName parsed))
			throw new BackendItemNotFoundException($"local-files: message '{item.Value}' no longer exists.");

		string path = Path.Combine(directory.FullName, fileName);
		(long Length, long WriteTicks)? stat = await AtomicFile.StatAsync(path, ct).ConfigureAwait(false);
		if (stat is null)
			throw new BackendItemNotFoundException($"local-files: message '{item.Value}' no longer exists.");

		if (expected is { } expectedRevision)
		{
			string current = MailFileName.RevisionOf(
				parsed.Flags, parsed.Categories, stat.Value.Length, stat.Value.WriteTicks);
			if (!string.Equals(current, expectedRevision.Value, StringComparison.Ordinal))
				throw new BackendPreconditionFailedException(
					$"local-files: message '{item.Value}' is at revision {current}, " +
					$"not the expected {expectedRevision.Value}.");
		}

		MailFlags flags = parsed.Flags with
		{
			Seen = patch.Read.HasValue ? patch.Read.Value : parsed.Flags.Seen,
			Flagged = patch.Flagged.HasValue ? patch.Flagged.Value : parsed.Flags.Flagged
		};
		IReadOnlyList<string> categories = patch.Categories.HasValue ? patch.Categories.Value : parsed.Categories;

		string renamed = MailFileName.Compose(parsed.Key, flags, categories, out IReadOnlyList<string> stored);
		if (!string.Equals(renamed, fileName, StringComparison.Ordinal))
		{
			string destination = Path.Combine(directory.FullName, renamed);
			if (!await AtomicFile.MoveAsync(path, destination, ct).ConfigureAwait(false))
				throw new BackendItemNotFoundException($"local-files: message '{item.Value}' no longer exists.");
			path = destination;
			Invalidate(directory);
			watcher.NotifyChanged(directory.FullName);
		}

		// The revision reports the categories that were actually STORED, not the ones requested:
		// an over-long set is trimmed to fit the name, and a revision describing the request would
		// never match the next enumeration — re-sending the message on every round forever.
		return await RevisionAsync(path, flags, stored, ct).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
		FolderKey folder, ItemKey item, MailItem value, CancellationToken ct)
	{
		RequireDrafts(folder, "rewrite a draft in");
		DirectoryInfo directory = tree.Resolve(folder);
		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		if (!index.Files.TryGetValue(item.Value, out string? fileName)
		    || !MailFileName.TryParse(fileName, out MailFileName parsed))
			throw new BackendItemNotFoundException($"local-files: draft '{item.Value}' no longer exists.");

		// The key is deliberately KEPT. The contract permits a moved key (IMAP rewrites as delete +
		// append), but the host stores the returned revision under the OLD key — so a store that can
		// rewrite in place must also report the revision that the next enumeration will report for
		// that same key, which means re-stating AFTER the write.
		MailFlags flags = value.Flags with { Draft = true };
		string renamed = MailFileName.Compose(parsed.Key, flags, value.Categories, out IReadOnlyList<string> stored);
		string path = Path.Combine(directory.FullName, renamed);
		await AtomicFile.WriteAsync(path, value.Rfc822, ct).ConfigureAwait(false);
		if (!string.Equals(renamed, fileName, StringComparison.Ordinal))
			AtomicFile.TryDelete(Path.Combine(directory.FullName, fileName));

		Invalidate(directory);
		watcher.NotifyChanged(directory.FullName);
		return (item, await RevisionAsync(path, flags, stored, ct).ConfigureAwait(false));
	}

	/// <inheritdoc />
	public async Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		if (!index.Files.TryGetValue(item.Value, out string? fileName))
			return;

		string path = Path.Combine(directory.FullName, fileName);
		DirectoryInfo? trash = permanent ? null : tree.SpecialFolder(FolderType.DeletedItems);
		bool alreadyInTrash = trash is not null
		                      && string.Equals(trash.FullName, directory.FullName, StringComparison.OrdinalIgnoreCase);
		if (trash is null || alreadyInTrash)
		{
			AtomicFile.TryDelete(path);
		}
		else
		{
			await MoveFileAsync(path, fileName, trash, ct).ConfigureAwait(false);
			Invalidate(trash);
			watcher.NotifyChanged(trash.FullName);
		}

		Invalidate(directory);
		watcher.NotifyChanged(directory.FullName);
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
	{
		DateTime watchStartUtc = DateTime.UtcNow;
		Dictionary<string, FolderKey> byDirectory = new(StringComparer.OrdinalIgnoreCase);
		foreach (FolderKey folder in folders)
			if (OwnsKey(folder))
				byDirectory[tree.Resolve(folder).FullName] = folder;

		if (byDirectory.Count == 0)
			return [];

		IReadOnlyList<string> changed = await watcher
			.WaitAsync([.. byDirectory.Keys], timeout, watchStartUtc, ct)
			.ConfigureAwait(false);
		return [.. changed.Where(byDirectory.ContainsKey).Select(directory => byDirectory[directory])];
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> MoveItemAsync(
		FolderKey source, ItemKey item, FolderKey destination, CancellationToken ct)
	{
		DirectoryInfo from = tree.Resolve(source);
		DirectoryInfo to = tree.Resolve(destination);
		to.Create();

		FolderIndex index = await IndexAsync(from, ct).ConfigureAwait(false);
		if (!index.Files.TryGetValue(item.Value, out string? fileName)
		    || !MailFileName.TryParse(fileName, out MailFileName parsed))
			throw new BackendItemNotFoundException($"local-files: message '{item.Value}' no longer exists.");

		string moved = await MoveFileAsync(Path.Combine(from.FullName, fileName), fileName, to, ct)
			.ConfigureAwait(false);
		Invalidate(from);
		Invalidate(to);
		watcher.NotifyChanged(from.FullName);
		watcher.NotifyChanged(to.FullName);

		if (!MailFileName.TryParse(moved, out MailFileName movedName))
			throw new BackendException($"local-files: moved message '{item.Value}' landed under an unusable name.");

		// The revision the DESTINATION would report — a manufactured value could never match the
		// next enumeration there, and would re-send the item as a spurious Change.
		return (new ItemKey(movedName.Key),
			await RevisionAsync(
					Path.Combine(to.FullName, moved), movedName.Flags, movedName.Categories, ct)
				.ConfigureAwait(false));
	}

	/// <inheritdoc />
	public Task<FolderKey> CreateFolderAsync(FolderKey? parent, string displayName, CancellationToken ct)
	{
		string name = displayName.Trim();
		if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Trim('.').Length == 0
		    || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			throw new BackendException($"local-files: '{displayName}' is not a usable folder name.");

		DirectoryInfo parentDirectory = parent is { } key ? tree.Resolve(key) : new DirectoryInfo(tree.MailRoot);
		DirectoryInfo created = new(Path.Combine(parentDirectory.FullName, name));
		if (created.Exists)
			throw new BackendException($"local-files: folder '{displayName}' already exists.");

		created.Create();
		watcher.NotifyChanged(parentDirectory.FullName);
		return Task.FromResult(tree.KeyOf(created));
	}

	/// <inheritdoc />
	public Task RenameFolderAsync(FolderKey folder, string newDisplayName, CancellationToken ct)
	{
		// Not supported, on purpose. This store's folder key IS the directory path, and the contract
		// requires a key to survive a rename — renaming the directory would change the key, which the
		// host reads as "the old folder was deleted and a new one appeared": every device would lose
		// that collection's sync state. The host turns this into FolderUpdate Status 3.
		throw new BackendException(
			"local-files: renaming a folder is not supported — its key is its path. " +
			"Create the new folder and move the messages into it.");
	}

	/// <inheritdoc />
	public Task DeleteFolderAsync(FolderKey folder, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		if (tree.IsTopLevel(directory) && MailFolderTree.Classify(directory.Name) != FolderType.UserMail)
			throw new BackendException($"local-files: '{directory.Name}' is a special folder and cannot be deleted.");

		if (directory.Exists)
			directory.Delete(true);
		Invalidate(directory);
		watcher.NotifyChanged(directory.Parent?.FullName ?? tree.MailRoot);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public async Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct)
	{
		DirectoryInfo sent = tree.SpecialFolder(FolderType.SentItems)
		                     ?? throw new BackendException("local-files: no Sent folder to save into.");
		string fileName = MailFileName.Compose(
			ItemKeyMint.Mint(), new MailFlags { Seen = true }, [], out _);
		await AtomicFile.WriteAsync(Path.Combine(sent.FullName, fileName), rfc822, ct).ConfigureAwait(false);
		Invalidate(sent);
		watcher.NotifyChanged(sent.FullName);
	}

	/// <inheritdoc />
	public async Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(
		FolderKey folder, ItemKey item, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		if (!index.Files.TryGetValue(item.Value, out string? fileName))
			return null;

		byte[]? bytes = await AtomicFile
			.TryReadAllBytesAsync(Path.Combine(directory.FullName, fileName), ct)
			.ConfigureAwait(false);
		return bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
	}

	/// <inheritdoc />
	public async Task SetAnsweredAsync(FolderKey folder, ItemKey item, bool forwarded, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		FolderIndex index = await IndexAsync(directory, ct).ConfigureAwait(false);
		if (!index.Files.TryGetValue(item.Value, out string? fileName)
		    || !MailFileName.TryParse(fileName, out MailFileName parsed))
			return;

		MailFlags flags = forwarded
			? parsed.Flags with { Forwarded = true }
			: parsed.Flags with { Answered = true };
		string renamed = MailFileName.Compose(parsed.Key, flags, parsed.Categories, out _);
		if (string.Equals(renamed, fileName, StringComparison.Ordinal))
			return;

		await AtomicFile
			.MoveAsync(Path.Combine(directory.FullName, fileName), Path.Combine(directory.FullName, renamed), ct)
			.ConfigureAwait(false);
		Invalidate(directory);
		watcher.NotifyChanged(directory.FullName);
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<SearchHit>> SearchAsync(
		FolderKey? folder, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct)
	{
		// A plain substring scan: this plugin has no MIME decoder (it references the contract and
		// nothing else), so a base64 or quoted-printable body will not match. Bounded on every axis
		// — newest first, a byte cap per message, and it stops at maxResults — because a null folder
		// means the WHOLE mailbox.
		List<DirectoryInfo> directories = [];
		if (folder is { } single)
			directories.Add(tree.Resolve(single));
		else
			foreach (BackendFolder mailFolder in tree.List())
				directories.Add(tree.Resolve(mailFolder.Key));

		List<(DirectoryInfo Directory, FileInfo File)> candidates = [];
		foreach (DirectoryInfo directory in directories)
		{
			if (!directory.Exists)
				continue;
			foreach (FileInfo file in AtomicFile.EnumerateItemFiles(directory, MailFileName.Extension))
			{
				if (since is { } window && file.LastWriteTimeUtc < window.UtcDateTime)
					continue;
				candidates.Add((directory, file));
			}
		}

		List<SearchHit> hits = [];
		foreach ((DirectoryInfo directory, FileInfo file) in candidates
			         .OrderByDescending(candidate => candidate.File.LastWriteTimeUtc))
		{
			if (hits.Count >= maxResults)
				break;
			ct.ThrowIfCancellationRequested();
			if (!MailFileName.TryParse(file.Name, out MailFileName parsed))
				continue;
			if (!await MatchesAsync(file, freeText, ct).ConfigureAwait(false))
				continue;
			hits.Add(new SearchHit { Folder = tree.KeyOf(directory), Item = new ItemKey(parsed.Key) });
		}

		return hits;
	}

	/// <inheritdoc />
	public Task EmptyFolderAsync(FolderKey folder, CancellationToken ct)
	{
		DirectoryInfo directory = tree.Resolve(folder);
		if (!directory.Exists)
			return Task.CompletedTask;

		foreach (FileInfo file in AtomicFile.EnumerateItemFiles(directory, MailFileName.Extension))
		{
			ct.ThrowIfCancellationRequested();
			AtomicFile.TryDelete(file.FullName);
		}

		Invalidate(directory);
		watcher.NotifyChanged(directory.FullName);
		return Task.CompletedTask;
	}

	private void RequireDrafts(FolderKey folder, string what)
	{
		if (!tree.IsOfType(folder, FolderType.Drafts))
			throw new BackendException($"local-files: cannot {what} '{folder.Value}' — it is not the Drafts folder.");
	}

	private async Task<MailItem?> ReadAsync(
		DirectoryInfo directory, FolderIndex index, ItemKey item, CancellationToken ct)
	{
		if (!index.Files.TryGetValue(item.Value, out string? fileName)
		    || !MailFileName.TryParse(fileName, out MailFileName parsed))
			return null;

		string path = Path.Combine(directory.FullName, fileName);
		byte[]? bytes = await AtomicFile.TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
		if (bytes is null)
			return null;

		(long Length, long WriteTicks)? stat = await AtomicFile.StatAsync(path, ct).ConfigureAwait(false);
		return new MailItem
		{
			Rfc822 = bytes,
			Flags = parsed.Flags,
			Categories = parsed.Categories,
			// The file's write time stands in for INTERNALDATE. A rename (a flag change) preserves
			// it, so a message's delivery time does not drift when it is marked read.
			Received = stat is null
				? null
				: new DateTimeOffset(new DateTime(stat.Value.WriteTicks, DateTimeKind.Utc))
		};
	}

	private static async Task<ItemRevision> RevisionAsync(
		string path, MailFlags flags, IReadOnlyList<string> categories, CancellationToken ct)
	{
		(long Length, long WriteTicks)? stat = await AtomicFile.StatAsync(path, ct).ConfigureAwait(false);
		if (stat is null)
			throw new BackendItemNotFoundException($"local-files: '{Path.GetFileName(path)}' vanished mid-write.");
		return new ItemRevision(
			MailFileName.RevisionOf(flags, categories, stat.Value.Length, stat.Value.WriteTicks));
	}

	private async Task<bool> MatchesAsync(FileInfo file, string freeText, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(freeText))
			return true;

		byte[]? bytes = await AtomicFile.TryReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
		if (bytes is null)
			return false;

		int length = Math.Min(bytes.Length, options.MaxSearchFileBytes);
		string text = System.Text.Encoding.UTF8.GetString(bytes, 0, length);
		return text.Contains(freeText, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Moves a message into another directory, minting a fresh key when the name is taken.</summary>
	private static async Task<string> MoveFileAsync(
		string sourcePath, string fileName, DirectoryInfo destination, CancellationToken ct)
	{
		string candidate = fileName;
		if (File.Exists(Path.Combine(destination.FullName, candidate))
		    && MailFileName.TryParse(fileName, out MailFileName parsed))
			candidate = MailFileName.Compose(ItemKeyMint.Mint(), parsed.Flags, parsed.Categories, out _);

		await AtomicFile.MoveAsync(sourcePath, Path.Combine(destination.FullName, candidate), ct)
			.ConfigureAwait(false);
		return candidate;
	}

	/// <summary>
	///   The folder's key → file-name index, rebuilt whenever the directory's cheap signature moves.
	///   Building it is also where ADOPTION happens: a file this store does not recognise is renamed
	///   once to a minted key, and a file whose key duplicates one already seen is re-keyed.
	/// </summary>
	private async Task<FolderIndex> IndexAsync(DirectoryInfo directory, CancellationToken ct)
	{
		if (!directory.Exists)
			return new FolderIndex("-", new Dictionary<string, string>(StringComparer.Ordinal));

		string signature = FileTreeWatcher.Signature(directory.FullName);
		lock (_indexGate)
			if (_indexes.TryGetValue(directory.FullName, out FolderIndex? cached)
			    && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
				return cached;

		Dictionary<string, string> files = new(StringComparer.Ordinal);
		bool renamed = false;
		foreach (FileInfo file in AtomicFile.EnumerateItemFiles(directory, MailFileName.Extension))
		{
			ct.ThrowIfCancellationRequested();
			bool recognised = MailFileName.TryParse(file.Name, out MailFileName parsed);
			if (recognised && !files.ContainsKey(parsed.Key))
			{
				files[parsed.Key] = file.Name;
				continue;
			}

			// Adopt: a hand-dropped message, or a copy that duplicated an existing key. A dropped
			// file keeps nothing but its bytes — there is no metadata in its name to preserve.
			string key = ItemKeyMint.Mint();
			string adopted = MailFileName.Compose(
				key,
				recognised ? parsed.Flags : new MailFlags(),
				recognised ? parsed.Categories : [],
				out _);
			if (await AtomicFile.MoveAsync(file.FullName, Path.Combine(directory.FullName, adopted), ct)
				    .ConfigureAwait(false))
			{
				files[key] = adopted;
				renamed = true;
			}
		}

		FolderIndex index = new(
			renamed ? FileTreeWatcher.Signature(directory.FullName) : signature, files);
		lock (_indexGate)
			_indexes[directory.FullName] = index;
		return index;
	}

	private void Invalidate(DirectoryInfo directory)
	{
		lock (_indexGate)
			_indexes.Remove(directory.FullName);
	}

	private sealed record FolderIndex(string Signature, Dictionary<string, string> Files);
}
