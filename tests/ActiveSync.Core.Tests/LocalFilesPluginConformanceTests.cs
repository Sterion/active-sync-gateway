using ActiveSync.Contracts;
using ActiveSync.Contracts.Conformance;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Plugins;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The filesystem plugin (<c>tests/ActiveSync.Plugin.Local</c>) loaded exactly as an out-of-repo
///   one would be, with the published conformance kit run against EVERY store it serves.
///   <para>
///     Where <see cref="PluginConformanceTests" /> proves one store class is reachable through the
///     loader, this proves the WHOLE store surface is: five content classes plus submission, from a
///     single plugin assembly that references nothing but <c>ActiveSync.Contracts</c>. The extra
///     assertions here cover what the kit deliberately cannot — that the five key spaces are
///     mutually disjoint (the kit tests one store against a synthetic foreign key), that the host
///     can derive five distinct content classes, and that the mail store satisfies the
///     <see cref="IMailboxOperations" /> requirement the session build enforces.
///   </para>
/// </summary>
public sealed class LocalFilesPluginConformanceTests : IDisposable
{
	private const string PluginName = "ActiveSync.Plugin.Local";
	private const string ProviderName = "local-files";

	private static readonly string StagedPluginDll =
		Path.Combine(AppContext.BaseDirectory, "localfilesplugin", PluginName + ".dll");

	private static readonly BackendCredentials Credentials =
		new() { UserName = "user1@example.com", Password = "pass" };

	private readonly string _pluginsRoot =
		Path.Combine(Path.GetTempPath(), $"as-lfs-plugins-{Guid.NewGuid():N}");

	private readonly string _dataRoot =
		Path.Combine(Path.GetTempPath(), $"as-lfs-data-{Guid.NewGuid():N}");

	public void Dispose()
	{
		foreach (string directory in (string[]) [_pluginsRoot, _dataRoot])
			try
			{
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A non-collectible load context keeps the plugin DLL mapped for the process lifetime.
			}
	}

	[Fact]
	public async Task PluginLoads_AndFillsEveryRoleItDeclares()
	{
		IBackendConnection connection = await OpenConnectionAsync();

		Assert.Equal(5, connection.Stores.Count);
		Assert.NotNull(connection.MailSubmit);
		// It does not serve Oof, and must not pretend to.
		Assert.Null(connection.Oof);
	}

	[Fact]
	public async Task EveryStore_SatisfiesTheStoreConformanceKit()
	{
		IBackendConnection connection = await OpenConnectionAsync();

		foreach (IContentStore store in connection.Stores)
		{
			ConformanceReport report = await StoreConformance.RunAsync(
				store,
				// Short: the wait check proves the timeout is HONOURED, not how fast push is.
				new ConformanceOptions { WaitTimeout = TimeSpan.FromMilliseconds(250) },
				CancellationToken.None);

			Assert.True(report.Passed, $"{store.GetType().Name}: {report}");
		}
	}

	/// <summary>
	///   The kit must not be vacuously green: the payload stores really ran the lifecycle, and their
	///   <c>expected</c> precondition is PASSED rather than the "store ignores it" skip — a
	///   content-derived revision makes the check possible, so this store honours it.
	/// </summary>
	[Fact]
	public async Task PayloadStores_ExercisedTheLifecycle_AndHonouredThePrecondition()
	{
		IBackendConnection connection = await OpenConnectionAsync();

		foreach (IContentStore store in connection.Stores.Where(store => store is not IMailStore))
		{
			ConformanceReport report = await StoreConformance.RunAsync(
				store,
				new ConformanceOptions { WaitTimeout = TimeSpan.FromMilliseconds(250) },
				CancellationToken.None);

			foreach (string name in (string[])
			         ["item.create", "item.create-is-visible", "item.round-trips",
				         "items.batch-null-is-not-fetched", "item.update", "item.update-precondition",
				         "item.delete"])
				Assert.Equal(
					ConformanceOutcome.Passed,
					Assert.Single(report.Checks, check => check.Name == name).Outcome);
		}
	}

	/// <summary>
	///   The kit skips the item lifecycle for a mail store (it will not synthesize a draft into a
	///   folder it cannot know is Drafts), so its folder-level checks are what has to hold here.
	/// </summary>
	[Fact]
	public async Task MailStore_PassesTheFolderChecks_AndSkipsTheLifecycleByDesign()
	{
		IBackendConnection connection = await OpenConnectionAsync();
		IContentStore mail = Assert.Single(connection.Stores, store => store is IMailStore);

		ConformanceReport report = await StoreConformance.RunAsync(
			mail,
			new ConformanceOptions { WaitTimeout = TimeSpan.FromMilliseconds(250) },
			CancellationToken.None);

		Assert.True(report.Passed, report.ToString());
		Assert.Equal(
			ConformanceOutcome.Skipped,
			Assert.Single(report.Checks, check => check.Name == "items.lifecycle").Outcome);
	}

	/// <summary>
	///   Key spaces must be disjoint ACROSS the stores of one session — the session dispatches a
	///   folder key to the first store that claims it, so an overlap would route a calendar write
	///   into the notes store. The kit can only test one store against a synthetic foreign key;
	///   this tests the real five against each other.
	/// </summary>
	[Fact]
	public async Task StoreKeySpaces_AreMutuallyDisjoint()
	{
		IBackendConnection connection = await OpenConnectionAsync();

		foreach (IContentStore owner in connection.Stores)
		foreach (BackendFolder folder in await owner.ListFoldersAsync(CancellationToken.None))
		foreach (IContentStore other in connection.Stores.Where(store => !ReferenceEquals(store, owner)))
			Assert.False(
				other.OwnsKey(folder.Key),
				$"{other.GetType().Name} claims {owner.GetType().Name}'s folder key '{folder.Key.Value}'");
	}

	/// <summary>
	///   The host derives a store's content class from the single alias interface it implements, and
	///   rejects a store implementing none or two. Five stores must therefore yield five classes.
	/// </summary>
	[Fact]
	public async Task EveryStore_DerivesADistinctContentClass()
	{
		IBackendConnection connection = await OpenConnectionAsync();

		string[] classes = [.. connection.Stores.Select(ContentStoreClasses.EasClassOf)];

		Assert.Equal(5, classes.Length);
		Assert.Equal(classes.Length, classes.Distinct(StringComparer.Ordinal).Count());
	}

	/// <summary>
	///   The session build hard-fails a MailStore whose store does not also implement
	///   <see cref="IMailboxOperations" />. Building a real composite session is the only way to
	///   prove this plugin clears that bar — and that its six role assignments really do collapse
	///   onto one connection.
	/// </summary>
	[Fact]
	public async Task CompositeSession_ResolvesMailMailboxAndSubmit()
	{
		BackendProviderRegistry registry = LoadPlugin();

		await using CompositeBackendSession session = await CompositeBackendSession.CreateAsync(
			registry, Credentials, 1, Credentials.UserName, RolesFor(_dataRoot), [],
			CancellationToken.None);

		Assert.NotNull(session.Mail);
		Assert.NotNull(session.Mailbox);
		Assert.NotNull(session.MailSubmit);

		// Six role assignments collapsed onto ONE connection, and the host resolved a store for
		// every content class from the single alias each store implements.
		foreach (string easClass in (string[]) ["Email", "Calendar", "Tasks", "Contacts", "Notes"])
			Assert.NotNull(session.GetStoreForClass(easClass));
	}

	/// <summary>
	///   The point of the whole plugin: drop a message file into the Inbox by hand and it syncs. The
	///   store adopts it by renaming it once to a minted key — and that key must then survive a flag
	///   change, because the host's snapshot and echo suppression are keyed on it (a key that moved
	///   would re-send the message as a delete plus an add on every read).
	/// </summary>
	[Fact]
	public async Task HandDroppedMessage_IsAdopted_WithAKeyStableAcrossAFlagChange()
	{
		IBackendConnection connection = await OpenConnectionAsync();
		IMailStore mail = Assert.Single(connection.Stores.OfType<IMailStore>());
		FolderKey inbox = InboxOf(await mail.ListFoldersAsync(CancellationToken.None));

		// A name no store would ever produce: spaces, a comma, no extension convention beyond .eml.
		await File.WriteAllTextAsync(
			Path.Combine(_dataRoot, Credentials.UserName, "mail", "Inbox", "Smith, John - invoice.eml"),
			"Subject: dropped by hand\r\n\r\nbody\r\n",
			CancellationToken.None);

		ItemKey key = Assert.Single(
			await mail.GetItemRevisionsAsync(inbox, ContentFilter.All, CancellationToken.None)).Key;

		MailItem? fetched = await mail.GetItemAsync(
			inbox, key, MailFetchOptions.Full, CancellationToken.None);
		Assert.NotNull(fetched);
		Assert.False(fetched.Flags.Seen);

		ItemRevision afterFlag = await mail.UpdateFlagsAsync(
			inbox, key, new MailFlagsPatch { Read = true }, null, CancellationToken.None);

		IReadOnlyDictionary<ItemKey, ItemRevision> revisions =
			await mail.GetItemRevisionsAsync(inbox, ContentFilter.All, CancellationToken.None);
		KeyValuePair<ItemKey, ItemRevision> listed = Assert.Single(revisions);

		Assert.Equal(key, listed.Key);
		// The revision the flag change returned is exactly what the enumeration now reports — the
		// host writes it straight into its snapshot, so anything else re-sends the message forever.
		Assert.Equal(afterFlag, listed.Value);

		MailItem? reread = await mail.GetItemAsync(
			inbox, key, MailFetchOptions.Full, CancellationToken.None);
		Assert.NotNull(reread);
		Assert.True(reread.Flags.Seen);
	}

	/// <summary>
	///   Two dropped files whose names collapse to the same stem must become two items. Deriving the
	///   key from the file name would make one of them silently disappear from sync.
	/// </summary>
	[Fact]
	public async Task TwoDroppedFilesSharingAStem_BecomeTwoDistinctItems()
	{
		IBackendConnection connection = await OpenConnectionAsync();
		IMailStore mail = Assert.Single(connection.Stores.OfType<IMailStore>());
		FolderKey inbox = InboxOf(await mail.ListFoldersAsync(CancellationToken.None));
		string inboxPath = Path.Combine(_dataRoot, Credentials.UserName, "mail", "Inbox");

		await File.WriteAllTextAsync(
			Path.Combine(inboxPath, "note.eml"), "Subject: one\r\n\r\none\r\n",
			CancellationToken.None);
		await File.WriteAllTextAsync(
			Path.Combine(inboxPath, "note.S.eml"), "Subject: two\r\n\r\ntwo\r\n",
			CancellationToken.None);

		IReadOnlyDictionary<ItemKey, ItemRevision> revisions =
			await mail.GetItemRevisionsAsync(inbox, ContentFilter.All, CancellationToken.None);

		Assert.Equal(2, revisions.Count);
	}

	private static FolderKey InboxOf(IReadOnlyList<BackendFolder> folders)
	{
		return Assert.Single(folders, folder => folder.Type == FolderType.Inbox).Key;
	}

	private static IReadOnlyList<ResolvedRole> RolesFor(string dataRoot)
	{
		ProviderSettings settings = ProviderSettings.FromFlat(new Dictionary<string, string?>
		{
			["RootPath"] = Path.Combine(dataRoot, "{user}"),
			["BasePath"] = dataRoot,
			// The conformance kit's wait check runs with a 250 ms timeout; a 1 s poll is the floor.
			["PollSeconds"] = "1"
		});

		return
		[
			.. new[]
				{
					BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar,
					BackendRole.Tasks, BackendRole.Contacts, BackendRole.Notes
				}
				.Select(role => new ResolvedRole
				{
					Role = role,
					ProviderName = ProviderName,
					Settings = settings,
					Credentials = Credentials
				})
		];
	}

	private BackendProviderRegistry LoadPlugin()
	{
		Assert.True(File.Exists(StagedPluginDll),
			$"plugin not staged at {StagedPluginDll} — check the StageLocalFilesPlugin build target");
		string pluginDir = Path.Combine(_pluginsRoot, PluginName);
		Directory.CreateDirectory(pluginDir);
		// The loader requires the entry assembly to be named after its directory.
		File.Copy(StagedPluginDll, Path.Combine(pluginDir, PluginName + ".dll"), true);

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ActiveSync:Plugins:Directory"] = _pluginsRoot
			})
			.Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<BackendProviderRegistry>();
		PluginLoader.LoadInto(services, configuration, NullLogger.Instance);
		return services.BuildServiceProvider().GetRequiredService<BackendProviderRegistry>();
	}

	private async Task<IBackendConnection> OpenConnectionAsync()
	{
		IBackendProvider provider = LoadPlugin().GetFor(ProviderName, BackendRole.MailStore);

		// Deliberately not disposed: the stores ARE the state the caller exercises afterwards, and
		// the provider — not the connection — owns the only OS handle (the change watcher).
		return await provider.CreateConnectionAsync(
			new BackendConnectionContext
			{
				GatewayCredentials = Credentials,
				GatewayUserId = 1,
				MailAddress = Credentials.UserName,
				Roles = RolesFor(_dataRoot),
				SharedCollections = []
			},
			CancellationToken.None);
	}
}
