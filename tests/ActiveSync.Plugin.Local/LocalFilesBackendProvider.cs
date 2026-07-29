using System.Collections.Concurrent;
using ActiveSync.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   The "local-files" provider: every content role plus submission, served out of one directory
///   tree per account.
/// </summary>
/// <remarks>
///   <para>
///     It deliberately does NOT implement <see cref="ICredentialVerifier" />. A directory holds no
///     credentials, and inventing a password file would be a second, worse credential store; with
///     no verifier the gateway falls back to deciding logins locally, which means a user declared
///     with a gateway password (<c>eas user password &lt;login&gt;</c>) — see docs/plugins.md.
///   </para>
///   <para>
///     Push watchers are owned HERE, one per (login, root), not by the stores: a connection's
///     disposal would otherwise tear a watcher down under a long-poll still parked on it. That is
///     the same shape the IMAP provider uses for its shared IDLE watchers, down to
///     <see cref="IPerUserResourceOwner" /> trimming them when a user's last session goes.
///   </para>
/// </remarks>
public sealed class LocalFilesBackendProvider
	: IBackendProvider, IPerUserResourceOwner, IWatcherDiagnostics, IReadinessSource, IDisposable
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole>
	{
		BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar,
		BackendRole.Tasks, BackendRole.Contacts, BackendRole.Notes
	};

	private readonly ConcurrentDictionary<string, WatcherEntry> _watchers = new(StringComparer.Ordinal);
	private readonly ILogger _logger;

	/// <summary>Creates the provider. Logging is optional so the plugin loads in a bare container too.</summary>
	public LocalFilesBackendProvider(ILoggerFactory? loggerFactory = null)
	{
		_logger = loggerFactory?.CreateLogger("ActiveSync.Plugin.Local") ?? NullLogger.Instance;
	}

	/// <inheritdoc />
	public string Name => "local-files";

	/// <inheritdoc />
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	/// <inheritdoc />
	public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
	{
		LocalFilesOptions options = EffectiveOptions(context);
		string login = context.GatewayCredentials.UserName;
		string root = RootPathResolver.Resolve(options, login);
		if (options.CreateMissingFolders)
			Directory.CreateDirectory(root);

		FileTreeWatcher watcher = WatcherFor(login, root, options);
		MailFolderTree tree = new(root, options);

		List<IContentStore> stores = [];
		IMailSubmitOperations? submit = null;
		foreach (ResolvedRole role in context.Roles)
			switch (role.Role)
			{
				case BackendRole.MailStore:
					tree.EnsureSpecialFolders();
					stores.Add(new LocalFilesMailStore(options, tree, watcher));
					break;
				case BackendRole.MailSubmit:
					tree.EnsureSpecialFolders();
					submit = new LocalFilesMailSubmit(tree, watcher);
					break;
				case BackendRole.Calendar:
					stores.Add(new LocalFilesCalendarStore(root, options, watcher));
					break;
				case BackendRole.Tasks:
					stores.Add(new LocalFilesTaskStore(root, options, watcher));
					break;
				case BackendRole.Contacts:
					stores.Add(new LocalFilesContactStore(root, options, watcher));
					break;
				case BackendRole.Notes:
					stores.Add(new LocalFilesNotesStore(root, options, watcher));
					break;
				default:
					throw new InvalidOperationException($"local-files cannot serve the {role.Role} role.");
			}

		// The watcher is NOT an owned resource: it belongs to the provider and outlives this
		// connection, which is the whole point of holding it here.
		return Task.FromResult<IBackendConnection>(new BackendConnection(stores, submit));
	}

	/// <inheritdoc />
	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		// Shape only, and deliberately no filesystem I/O: this runs for every declared user on every
		// settings-snapshot rebuild, so a stat per call would be a syscall storm against a path that
		// may legitimately not exist yet.
		RootPathResolver.ValidateTemplate(settings.Bind<LocalFilesOptions>(), role, failures);
	}

	/// <inheritdoc />
	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		LocalFilesOptions options = settings.Bind<LocalFilesOptions>();
		string root = string.IsNullOrWhiteSpace(options.RootPath) ? "(unset)" : options.RootPath;
		return role == BackendRole.MailSubmit
			? $"filesystem {root} (sends loop back to the sender's Inbox)"
			: $"filesystem {root}";
	}

	/// <inheritdoc />
	public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role)
	{
		// Every field is administration-only (SelfServiceEditable stays false): a path template
		// decides WHERE the gateway reads and writes, and a portal user is the lowest privilege
		// level in the system.
		return
		[
			new BackendConfigField
			{
				Name = "RootPath", Label = "Store root", Type = BackendFieldType.String, Required = true,
				Help = "Absolute directory holding this account's mail, calendar, tasks, contacts and notes. " +
				       "Supports the placeholders {user} (the gateway login) and {localpart} (the login up to " +
				       "'@'); a path with neither gets the login appended, so accounts never share a tree."
			},
			new BackendConfigField
			{
				Name = "BasePath", Label = "Containment root", Type = BackendFieldType.String,
				Help = "Optional absolute directory the resolved store root must stay under. Set it when " +
				       "RootPath carries a placeholder: it is what stops an unusual login from reaching " +
				       "outside the tree."
			},
			new BackendConfigField
			{
				Name = "CreateMissingFolders", Label = "Create missing folders",
				Type = BackendFieldType.Bool, Default = "true",
				Help = "Create the store root and the Inbox/Drafts/Sent/Trash directories on first use."
			},
			new BackendConfigField
			{
				Name = "PollSeconds", Label = "Poll interval (seconds)", Type = BackendFieldType.Int,
				Default = "5", Min = 1, Max = 300,
				Help = "How often a waiting client re-checks the directories. This is the backstop behind " +
				       "the filesystem watcher, which reports nothing on some network and container filesystems."
			},
			new BackendConfigField
			{
				Name = "MaxSearchFileBytes", Label = "Search read limit (bytes)", Type = BackendFieldType.Int,
				Default = "1048576", Min = 1024,
				Help = "How much of each message a mailbox search reads. The search is a plain substring " +
				       "scan, so encoded (base64 / quoted-printable) bodies do not match."
			}
		];
	}

	/// <inheritdoc />
	public void TrimUserResources(IReadOnlySet<string> activeGatewayLogins)
	{
		foreach ((string key, WatcherEntry entry) in _watchers)
		{
			if (activeGatewayLogins.Contains(entry.Login))
				continue;
			if (_watchers.TryRemove(key, out WatcherEntry? removed))
				removed.Watcher.Dispose();
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<WatcherInfo> SnapshotWatchers()
	{
		return
		[
			.. _watchers.Values.Select(entry => new WatcherInfo
			{
				User = entry.Login,
				Resource = entry.Watcher.WatcherActive
					? entry.Watcher.Root
					: entry.Watcher.Root + " (polling)"
			})
		];
	}

	/// <inheritdoc />
	public Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct)
	{
		LocalFilesOptions options = settings.Bind<LocalFilesOptions>();
		try
		{
			// Probe the fixed prefix of the template — the per-user leaf legitimately does not exist
			// until that user first syncs, so its absence is not an outage.
			string template = string.IsNullOrWhiteSpace(options.BasePath) ? options.RootPath : options.BasePath;
			int placeholder = template.IndexOf('{', StringComparison.Ordinal);
			string prefix = placeholder < 0 ? template : template[..placeholder];
			string directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(prefix)) ?? prefix;
			return Task.FromResult(Directory.Exists(prefix) || Directory.Exists(directory));
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
		{
			return Task.FromResult(false);
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		foreach (WatcherEntry entry in _watchers.Values)
			entry.Watcher.Dispose();
		_watchers.Clear();
	}

	/// <summary>
	///   One connection has one root, but config can assign this provider six role sections with six
	///   different ones. The MailStore section wins (it is the account's anchor); a disagreement is
	///   logged rather than failed, because the roles are still individually valid.
	/// </summary>
	private LocalFilesOptions EffectiveOptions(BackendConnectionContext context)
	{
		ResolvedRole anchor = context.Roles.FirstOrDefault(role => role.Role == BackendRole.MailStore)
		                      ?? context.Roles[0];
		LocalFilesOptions options = anchor.Settings.Bind<LocalFilesOptions>();

		foreach (ResolvedRole role in context.Roles)
		{
			if (role == anchor)
				continue;
			string other = role.Settings.Bind<LocalFilesOptions>().RootPath;
			if (!string.IsNullOrWhiteSpace(other)
			    && !string.Equals(other, options.RootPath, StringComparison.Ordinal))
				_logger.LogWarning(
					"local-files: the {Role} role names RootPath '{Other}' but the connection uses " +
					"'{Root}' from the {Anchor} role — one connection has one root.",
					role.Role, other, options.RootPath, anchor.Role);
		}

		return options;
	}

	private FileTreeWatcher WatcherFor(string login, string root, LocalFilesOptions options)
	{
		string key = login + "\n" + root;
		return _watchers.GetOrAdd(
				key,
				_ => new WatcherEntry(login, new FileTreeWatcher(root, TimeSpan.FromSeconds(options.PollSeconds))))
			.Watcher;
	}

	private sealed record WatcherEntry(string Login, FileTreeWatcher Watcher);
}
