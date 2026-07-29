using System.Security.Cryptography;
using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   The shared implementation of the four PAYLOAD content classes (calendar, tasks, contacts,
///   notes) over a directory tree — the filesystem counterpart of the in-repo
///   <c>LocalStoreBase&lt;TItem&gt;</c>: the whole <see cref="IContentStore{TItem}" /> contract is
///   implemented once here, and a subclass supplies only its collection name, its file extension,
///   how to parse and build its payload, and where its UID lives.
/// </summary>
/// <remarks>
///   <para>
///     Layout is <c>&lt;root&gt;/&lt;collection&gt;/&lt;folder&gt;/&lt;stem&gt;&lt;extension&gt;</c>.
///     Each subdirectory of the class directory is one folder, so a second calendar is a second
///     directory and nothing else. The folder list is SORTED before the first one is crowned the
///     default type: directory enumeration order is a filesystem's whim, and a default collection
///     that moved between two syncs would re-type the folder on every device.
///   </para>
///   <para>
///     The item key is the file's stem and the payload text is stored VERBATIM — what the host
///     hands over is byte-for-byte what it gets back, which is the round-trip fidelity the contract
///     asks for and the reason properties EAS cannot express survive an edit here.
///   </para>
/// </remarks>
internal abstract class PayloadFileStore<TItem> : IContentStore<TItem> where TItem : class
{
	private readonly Lock _revisionGate = new();
	private readonly Dictionary<string, CachedRevision> _revisions = new(StringComparer.Ordinal);

	protected PayloadFileStore(string root, LocalFilesOptions options, FileTreeWatcher watcher)
	{
		Root = root;
		Options = options;
		Watcher = watcher;
	}

	/// <summary>The account's store root.</summary>
	protected string Root { get; }

	/// <summary>The provider's bound options.</summary>
	protected LocalFilesOptions Options { get; }

	/// <summary>The account's shared change watcher.</summary>
	protected FileTreeWatcher Watcher { get; }

	/// <summary>This store's folder-key prefix; must be disjoint from every other store's.</summary>
	protected abstract string KeyPrefix { get; }

	/// <summary>The class directory beneath the root ("calendar", "contacts", …).</summary>
	protected abstract string Collection { get; }

	/// <summary>The item file extension, including the dot.</summary>
	protected abstract string Extension { get; }

	/// <summary>Display name of the folder created when the class directory is empty.</summary>
	protected abstract string DefaultFolderName { get; }

	/// <summary>The EAS type of the first (default) folder.</summary>
	protected abstract FolderType DefaultFolderType { get; }

	/// <summary>The EAS type of every additional folder.</summary>
	protected abstract FolderType AdditionalFolderType { get; }

	/// <summary>Parses stored text into the payload record; <c>null</c> when it is unusable.</summary>
	protected abstract TItem? ParseContent(string content);

	/// <summary>
	///   Renders the payload to the text stored on disk. <paramref name="existingContent" /> is the
	///   file's current text on an update (<c>null</c> on create), for classes whose stored shape
	///   carries more than the payload does.
	/// </summary>
	protected abstract string BuildContent(TItem item, string? existingContent);

	/// <summary>The payload's own UID, used as the file stem so the file is recognisable. <c>null</c> when it has none.</summary>
	protected virtual string? ExtractUid(string content)
	{
		return null;
	}

	/// <inheritdoc />
	public bool OwnsKey(FolderKey key)
	{
		return key.Value.StartsWith(KeyPrefix, StringComparison.Ordinal);
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		DirectoryInfo classDirectory = new(Path.Combine(Root, Collection));
		if (!classDirectory.Exists)
		{
			if (!Options.CreateMissingFolders)
				return Task.FromResult<IReadOnlyList<BackendFolder>>([]);
			classDirectory.Create();
		}

		List<string> names = [.. classDirectory
			.EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
			.Select(directory => directory.Name)
			.Where(AtomicFile.IsItemFileName)
			.OrderBy(name => name, StringComparer.Ordinal)];

		if (names.Count == 0)
		{
			if (!Options.CreateMissingFolders)
				return Task.FromResult<IReadOnlyList<BackendFolder>>([]);
			Directory.CreateDirectory(Path.Combine(classDirectory.FullName, DefaultFolderName));
			names.Add(DefaultFolderName);
		}

		List<BackendFolder> folders = [];
		for (int index = 0; index < names.Count; index++)
			folders.Add(new BackendFolder
			{
				Key = new FolderKey(KeyPrefix + names[index]),
				DisplayName = names[index],
				Type = index == 0 ? DefaultFolderType : AdditionalFolderType
			});

		return Task.FromResult<IReadOnlyList<BackendFolder>>(folders);
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		// The date filter is deliberately not applied: the host only ever passes one for Email and
		// Calendar, the contract says older items MAY be omitted, and a store that never omits
		// produces no aged-out deletes to reconcile.
		DirectoryInfo directory = ResolveDirectory(folder);
		Dictionary<ItemKey, ItemRevision> revisions = [];
		if (!directory.Exists)
			return revisions;

		foreach (FileInfo file in AtomicFile.EnumerateItemFiles(directory, Extension))
		{
			ct.ThrowIfCancellationRequested();
			string? revision = await RevisionOfAsync(file, ct).ConfigureAwait(false);
			if (revision is null)
				// The file vanished between the enumeration and the read — a delete that the next
				// round reports anyway. Anything OTHER than a clean disappearance throws out of
				// RevisionOfAsync, because a silently dropped entry is a delete to every device.
				continue;
			revisions[new ItemKey(Path.GetFileNameWithoutExtension(file.Name))] = new ItemRevision(revision);
		}

		return revisions;
	}

	/// <inheritdoc />
	public async Task<TItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct)
	{
		string? content = await AtomicFile
			.TryReadAllTextAsync(PathOf(folder, item), ct)
			.ConfigureAwait(false);
		return content is null ? null : ParseContent(content);
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
		FolderKey folder, TItem item, CancellationToken ct)
	{
		DirectoryInfo directory = ResolveDirectory(folder);
		directory.Create();

		string content = BuildContent(item, null);
		string stem = ReserveStem(directory, StemFor(content));
		string path = Path.Combine(directory.FullName, stem + Extension);
		await AtomicFile.WriteAsync(path, System.Text.Encoding.UTF8.GetBytes(content), ct).ConfigureAwait(false);
		Watcher.NotifyChanged(directory.FullName);
		return (new ItemKey(stem), new ItemRevision(RevisionOf(content)));
	}

	/// <inheritdoc />
	public async Task<ItemRevision> UpdateItemAsync(
		FolderKey folder, ItemKey item, TItem value, ItemRevision? expected, CancellationToken ct)
	{
		string path = PathOf(folder, item);
		string? existing = await AtomicFile.TryReadAllTextAsync(path, ct).ConfigureAwait(false);
		if (existing is null)
			throw new BackendItemNotFoundException($"local-files: {Collection} item '{item.Value}' no longer exists.");

		// A content-derived revision means the precondition IS checkable here, so it is checked:
		// the host can then re-fetch, re-merge and retry once instead of silently overwriting a
		// change another device made between its read and this write.
		if (expected is { } expectedRevision)
		{
			string current = RevisionOf(existing);
			if (!string.Equals(current, expectedRevision.Value, StringComparison.Ordinal))
				throw new BackendPreconditionFailedException(
					$"local-files: {Collection} item '{item.Value}' is at revision {current}, " +
					$"not the expected {expectedRevision.Value}.");
		}

		string content = BuildContent(value, existing);
		await AtomicFile.WriteAsync(path, System.Text.Encoding.UTF8.GetBytes(content), ct).ConfigureAwait(false);
		Watcher.NotifyChanged(Path.GetDirectoryName(path) ?? Root);
		return new ItemRevision(RevisionOf(content));
	}

	/// <inheritdoc />
	public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
	{
		// `permanent` carries no meaning here: there is no per-class wastebasket to move a payload
		// item into, exactly as the DAV and local stores treat it.
		string path = PathOf(folder, item);
		AtomicFile.TryDelete(path);
		Watcher.NotifyChanged(Path.GetDirectoryName(path) ?? Root);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
	{
		// Captured as early as possible: a change stamped after this instant satisfies the wait,
		// which is what stops a change landing between two requests from being missed.
		DateTime watchStartUtc = DateTime.UtcNow;
		Dictionary<string, FolderKey> byDirectory = new(StringComparer.OrdinalIgnoreCase);
		foreach (FolderKey folder in folders)
			if (OwnsKey(folder))
				byDirectory[ResolveDirectory(folder).FullName] = folder;

		if (byDirectory.Count == 0)
			return [];

		IReadOnlyList<string> changed = await Watcher
			.WaitAsync([.. byDirectory.Keys], timeout, watchStartUtc, ct)
			.ConfigureAwait(false);

		return [.. changed.Where(byDirectory.ContainsKey).Select(directory => byDirectory[directory])];
	}

	/// <summary>The directory backing a folder key, with the key's own name validated.</summary>
	protected DirectoryInfo ResolveDirectory(FolderKey folder)
	{
		if (!OwnsKey(folder))
			throw new BackendException($"local-files: folder '{folder.Value}' is not a {Collection} folder.");

		string name = folder.Value[KeyPrefix.Length..];
		if (name.Length == 0 || name.Contains('/') || name.Contains('\\')
		    || name.Contains("..", StringComparison.Ordinal))
			throw new BackendException($"local-files: folder '{folder.Value}' is not a usable directory name.");

		return new DirectoryInfo(Path.Combine(Root, Collection, name));
	}

	private string PathOf(FolderKey folder, ItemKey item)
	{
		string stem = item.Value;
		if (stem.Length == 0 || stem.Contains('/') || stem.Contains('\\')
		    || stem.Contains("..", StringComparison.Ordinal) || stem[0] == '.')
			throw new BackendItemNotFoundException($"local-files: item key '{item.Value}' is not a usable file name.");
		return Path.Combine(ResolveDirectory(folder).FullName, stem + Extension);
	}

	/// <summary>
	///   The file stem for a new item: its own UID when it has a usable one, else a minted key. The
	///   UID is percent-ENCODED rather than stripped of awkward characters — stripping is not
	///   injective, and two UIDs collapsing onto one stem would silently overwrite one another's
	///   item. A dot-leading or over-long result falls back to a minted key.
	/// </summary>
	private string StemFor(string content)
	{
		string? uid = ExtractUid(content);
		if (string.IsNullOrWhiteSpace(uid))
			return ItemKeyMint.Mint();

		string encoded = Uri.EscapeDataString(uid.Trim());
		return encoded.Length == 0 || encoded.Length > 100 || encoded[0] == '.'
			? ItemKeyMint.Mint()
			: encoded;
	}

	/// <summary>
	///   Reserves a stem by creating the file empty and exclusively, suffixing on collision. The
	///   zero-length window is deliberate and safe: an enumeration catching it parses to nothing,
	///   which the contract reads as "not fetched" and retries, never as a delete.
	/// </summary>
	private string ReserveStem(DirectoryInfo directory, string preferred)
	{
		for (int attempt = 1; attempt <= 64; attempt++)
		{
			string stem = attempt == 1 ? preferred : $"{preferred}-{attempt}";
			string path = Path.Combine(directory.FullName, stem + Extension);
			try
			{
				using FileStream reservation = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				return stem;
			}
			catch (IOException)
			{
				// Taken — by an existing item, or by a concurrent create that won the race.
			}
		}

		throw new BackendException($"local-files: could not reserve a file name in '{directory.FullName}'.");
	}

	/// <summary>
	///   A content-derived revision: length plus a truncated SHA-256. Deliberately NOT the file's
	///   timestamp — two writes inside one filesystem timestamp tick (a millisecond on ext4, worse
	///   on some network filesystems) would produce the same revision and the second change would
	///   never reach a device.
	/// </summary>
	protected static string RevisionOf(string content)
	{
		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
		return $"{bytes.Length:x}-{Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant()}";
	}

	/// <summary>
	///   The revision of a file on disk, cached on (length, write time) so a Ping's enumeration
	///   re-hashes only what actually changed. <c>null</c> means the file vanished.
	/// </summary>
	private async Task<string?> RevisionOfAsync(FileInfo file, CancellationToken ct)
	{
		(long Length, long WriteTicks)? stat = await AtomicFile.StatAsync(file.FullName, ct).ConfigureAwait(false);
		if (stat is null)
			return null;

		lock (_revisionGate)
			if (_revisions.TryGetValue(file.FullName, out CachedRevision cached)
			    && cached.Length == stat.Value.Length && cached.WriteTicks == stat.Value.WriteTicks)
				return cached.Revision;

		string? content = await AtomicFile.TryReadAllTextAsync(file.FullName, ct).ConfigureAwait(false);
		if (content is null)
			return null;

		string revision = RevisionOf(content);
		lock (_revisionGate)
			_revisions[file.FullName] = new CachedRevision(stat.Value.Length, stat.Value.WriteTicks, revision);
		return revision;
	}

	private readonly record struct CachedRevision(long Length, long WriteTicks, string Revision);
}
