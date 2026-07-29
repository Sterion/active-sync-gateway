using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   Maps <c>&lt;root&gt;/mail</c> to and from folder keys, and decides each directory's EAS type.
/// </summary>
/// <remarks>
///   <para>
///     Classification is the STORE's job, not the host's: the host takes
///     <see cref="BackendFolder.Type" /> verbatim, and the write paths that need a Sent, Trash or
///     Drafts directory resolve it through this same table — so FolderSync's answer and the write
///     paths can never disagree. It is the filesystem counterpart of the IMAP backend's SPECIAL-USE
///     plus name matching.
///   </para>
///   <para>
///     Only top-level directories can be special. A nested <c>Archive/Sent</c> is a user folder,
///     the same way an IMAP server would not grant \Sent to a random subfolder.
///   </para>
/// </remarks>
internal sealed class MailFolderTree(string root, LocalFilesOptions options)
{
	/// <summary>The mail store's folder-key prefix; disjoint from every payload store's.</summary>
	public const string Prefix = "lfs-mail:";

	private static readonly string[] SentNames = ["Sent", "Sent Items", "Sent Messages"];
	private static readonly string[] TrashNames = ["Trash", "Deleted Items", "Deleted"];

	/// <summary>The mail directory: the parent of every mail folder.</summary>
	public string MailRoot { get; } = Path.Combine(root, "mail");

	/// <summary>
	///   Creates the four folders every client expects. Without them a fresh account has no Inbox
	///   to sync and no Sent folder for the host to save a sent message into.
	/// </summary>
	public void EnsureSpecialFolders()
	{
		if (!options.CreateMissingFolders)
			return;
		Directory.CreateDirectory(MailRoot);
		foreach (string name in new[] { "Inbox", "Drafts", "Sent", "Trash" })
			Directory.CreateDirectory(Path.Combine(MailRoot, name));
	}

	/// <summary>Every mail folder, nested directories included, parents before children.</summary>
	public IReadOnlyList<BackendFolder> List()
	{
		DirectoryInfo mailRoot = new(MailRoot);
		if (!mailRoot.Exists)
			return [];

		List<BackendFolder> folders = [];
		Walk(mailRoot, null, 1);
		return folders;

		void Walk(DirectoryInfo directory, FolderKey? parent, int depth)
		{
			foreach (DirectoryInfo child in directory
				         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
				         .Where(child => AtomicFile.IsItemFileName(child.Name))
				         .OrderBy(child => child.Name, StringComparer.Ordinal))
			{
				FolderKey key = KeyOf(child);
				folders.Add(new BackendFolder
				{
					Key = key,
					DisplayName = child.Name,
					ParentKey = parent,
					Type = depth == 1 ? Classify(child.Name) : FolderType.UserMail
				});
				Walk(child, key, depth + 1);
			}
		}
	}

	/// <summary>The folder key of a directory: its path relative to the mail root, always '/'-separated.</summary>
	/// <remarks>
	///   The separator is normalized because the host matches a folder's <c>ParentKey</c> against
	///   another folder's key by ordinal string comparison — a key built with '\' on Windows and a
	///   parent key built with '/' would silently reparent the folder to the root.
	/// </remarks>
	public FolderKey KeyOf(DirectoryInfo directory)
	{
		string relative = Path.GetRelativePath(MailRoot, directory.FullName);
		return new FolderKey(Prefix + relative.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/'));
	}

	/// <summary>The directory backing a folder key.</summary>
	/// <exception cref="BackendException">The key is not this store's, or names nothing usable.</exception>
	public DirectoryInfo Resolve(FolderKey folder)
	{
		if (!Owns(folder))
			throw new BackendException($"local-files: folder '{folder.Value}' is not a mail folder.");

		string relative = folder.Value[Prefix.Length..];
		if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal)
		                         || Path.IsPathRooted(relative))
			throw new BackendException($"local-files: folder '{folder.Value}' is not a usable directory name.");

		string combined = Path.GetFullPath(Path.Combine(MailRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
		string mailRootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(MailRoot));
		if (!combined.StartsWith(mailRootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
			throw new BackendException($"local-files: folder '{folder.Value}' falls outside the mail root.");

		return new DirectoryInfo(combined);
	}

	/// <summary>Whether the key belongs to this store.</summary>
	public bool Owns(FolderKey folder)
	{
		return folder.Value.StartsWith(Prefix, StringComparison.Ordinal);
	}

	/// <summary>The EAS type of a top-level folder name.</summary>
	public static FolderType Classify(string name)
	{
		if (Matches(name, "Inbox"))
			return FolderType.Inbox;
		if (Matches(name, "Drafts"))
			return FolderType.Drafts;
		if (Matches(name, "Outbox"))
			return FolderType.Outbox;
		if (SentNames.Any(candidate => Matches(name, candidate)))
			return FolderType.SentItems;
		if (TrashNames.Any(candidate => Matches(name, candidate)))
			return FolderType.DeletedItems;
		return FolderType.UserMail;
	}

	/// <summary>Whether the folder is of the given special type — the write paths' own gate.</summary>
	public bool IsOfType(FolderKey folder, FolderType type)
	{
		DirectoryInfo directory = Resolve(folder);
		return IsTopLevel(directory) && Classify(directory.Name) == type;
	}

	/// <summary>
	///   The directory of a special folder, creating it when missing (a mailbox needs somewhere to
	///   put a sent message even if the directory was removed underneath it). <c>null</c> when it
	///   does not exist and may not be created.
	/// </summary>
	public DirectoryInfo? SpecialFolder(FolderType type)
	{
		DirectoryInfo mailRoot = new(MailRoot);
		if (mailRoot.Exists)
			foreach (DirectoryInfo child in mailRoot
				         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
				         .OrderBy(child => child.Name, StringComparer.Ordinal))
				if (Classify(child.Name) == type)
					return child;

		if (!options.CreateMissingFolders)
			return null;

		string? name = type switch
		{
			FolderType.Inbox => "Inbox",
			FolderType.Drafts => "Drafts",
			FolderType.SentItems => "Sent",
			FolderType.DeletedItems => "Trash",
			_ => null
		};
		if (name is null)
			return null;

		DirectoryInfo created = new(Path.Combine(MailRoot, name));
		created.Create();
		return created;
	}

	/// <summary>Whether the directory sits directly under the mail root.</summary>
	public bool IsTopLevel(DirectoryInfo directory)
	{
		return string.Equals(
			Path.TrimEndingDirectorySeparator(directory.Parent?.FullName ?? ""),
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(MailRoot)),
			StringComparison.OrdinalIgnoreCase);
	}

	private static bool Matches(string name, string candidate)
	{
		return string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase);
	}
}
