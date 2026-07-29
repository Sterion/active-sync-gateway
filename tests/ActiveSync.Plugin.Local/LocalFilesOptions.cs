namespace ActiveSync.Plugin.Local;

/// <summary>
///   The "local-files" provider's own options, bound from its role section by the provider itself
///   (<c>ProviderSettings.Bind&lt;LocalFilesOptions&gt;()</c>). The host never knows this shape —
///   that is the whole point of a plugin carrying its own configuration.
/// </summary>
/// <remarks>
///   Every default here MUST equal the <c>Default</c> string the provider's
///   <c>DescribeConfiguration</c> declares for the same key: the admin UI renders that string as
///   the dimmed placeholder, so a drift would advertise a default the code does not use.
/// </remarks>
public sealed class LocalFilesOptions
{
	/// <summary>
	///   Absolute path of the per-user store root. Supports the placeholders <c>{user}</c> (the
	///   gateway login) and <c>{localpart}</c> (the login up to '@'); a template carrying neither
	///   gets the sanitized login appended as a subdirectory, so two accounts can never share a
	///   tree by accident.
	/// </summary>
	public string RootPath { get; set; } = "";

	/// <summary>
	///   Optional containment root. When set, a resolved <see cref="RootPath" /> that would land
	///   outside it is refused — the guard against a login carrying path separators (the gateway
	///   permits them in a login) escaping the tree through <c>{user}</c>.
	/// </summary>
	public string BasePath { get; set; } = "";

	/// <summary>Whether missing directories (the root, the class folders, the mail special folders) are created on connection open.</summary>
	public bool CreateMissingFolders { get; set; } = true;

	/// <summary>
	///   How often a long-poll re-stats the watched directories. This is the BACKSTOP, not the
	///   primary signal: a filesystem watcher provides sub-second push, but it silently reports
	///   nothing on many network and container filesystems, so the poll is what makes the wait
	///   correct everywhere.
	/// </summary>
	public int PollSeconds { get; set; } = 5;

	/// <summary>
	///   Largest slice of one message the mailbox search reads, in bytes. The search is a plain
	///   substring scan (the plugin has no MIME decoder), so this bounds the cost of a whole-mailbox
	///   query rather than expressing a protocol limit.
	/// </summary>
	public int MaxSearchFileBytes { get; set; } = 1048576;
}
