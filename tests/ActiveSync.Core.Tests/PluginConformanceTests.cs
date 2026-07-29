using ActiveSync.Contracts;
using ActiveSync.Contracts.Conformance;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Plugins;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The claim "ActiveSync.Contracts alone is enough to write a working backend", tested end to
///   end: the fixture plugin is loaded from a plugins directory exactly as an out-of-repo one
///   would be, its provider opens a connection, and the published conformance kit exercises the
///   store that connection returns.
///   <para>
///     This is the gap that let the old contract drift: the fixture implemented no store and threw
///     from <c>CreateConnectionAsync</c>, so nothing ever proved a plugin could sync — only that it
///     could register. Both halves are under test here, and the kit is exercised by its own repo
///     rather than shipped untested.
///   </para>
/// </summary>
public sealed class PluginConformanceTests : IDisposable
{
	private static readonly string StagedPluginDll =
		Path.Combine(AppContext.BaseDirectory, "testplugin", "ActiveSync.TestPlugin.dll");

	private readonly string _root =
		Path.Combine(Path.GetTempPath(), $"as-conformance-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// A non-collectible load context keeps the plugin DLL mapped for the process lifetime.
		}
	}

	[Fact]
	public async Task LoadedPluginStore_SatisfiesTheStoreConformanceKit()
	{
		IContentStore store = await OpenPluginStoreAsync();

		ConformanceReport report = await StoreConformance.RunAsync(
			store,
			// Short, because the wait check's job is to prove the timeout is HONOURED, not to
			// measure push latency — the fixture has nothing to notify about.
			new ConformanceOptions { WaitTimeout = TimeSpan.FromMilliseconds(250) },
			CancellationToken.None);

		Assert.True(report.Passed, report.ToString());
	}

	/// <summary>
	///   The kit must be able to FAIL, and its skip outcome must mean what it says. This pins both
	///   ends against the fixture: the precondition check runs for real (the fixture honours
	///   <c>expected</c> with a row version, so it is a pass, not the "store ignores it" skip), and
	///   the lifecycle checks ran rather than being skipped away.
	/// </summary>
	[Fact]
	public async Task ConformanceKit_ActuallyExercisedTheLifecycle_AndThePrecondition()
	{
		IContentStore store = await OpenPluginStoreAsync();

		ConformanceReport report = await StoreConformance.RunAsync(
			store,
			new ConformanceOptions { WaitTimeout = TimeSpan.FromMilliseconds(250) },
			CancellationToken.None);

		ConformanceCheck precondition = Assert.Single(
			report.Checks, check => check.Name == "item.update-precondition");
		Assert.Equal(ConformanceOutcome.Passed, precondition.Outcome);

		foreach (string name in (string[])
		         ["item.create", "item.create-is-visible", "item.round-trips",
			         "items.batch-null-is-not-fetched", "item.update", "item.delete"])
			Assert.Equal(
				ConformanceOutcome.Passed,
				Assert.Single(report.Checks, check => check.Name == name).Outcome);
	}

	/// <summary>
	///   A store that breaks an obligation must be REPORTED, not passed. Without this the kit could
	///   be vacuously green — the classic failure of a conformance suite nobody has seen fail.
	/// </summary>
	[Fact]
	public async Task ConformanceKit_ReportsAStoreThatBreaksAnObligation()
	{
		ConformanceReport report = await StoreConformance.RunAsync(
			new UnstableRevisionStore(), new ConformanceOptions { AllowMutation = false },
			CancellationToken.None);

		Assert.False(report.Passed);
		Assert.Contains(report.Failures, failure => failure.Name == "revisions.stable");
	}

	private async Task<IContentStore> OpenPluginStoreAsync()
	{
		Assert.True(File.Exists(StagedPluginDll),
			$"fixture plugin not staged at {StagedPluginDll} — check the StageTestPlugin build target");
		string pluginDir = Path.Combine(_root, "ActiveSync.TestPlugin");
		Directory.CreateDirectory(pluginDir);
		File.Copy(StagedPluginDll, Path.Combine(pluginDir, "ActiveSync.TestPlugin.dll"));

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["ActiveSync:Plugins:Directory"] = _root })
			.Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<BackendProviderRegistry>();
		PluginLoader.LoadInto(services, configuration, NullLogger.Instance);
		BackendProviderRegistry registry = services.BuildServiceProvider().GetRequiredService<BackendProviderRegistry>();

		IBackendProvider provider = registry.GetFor("testplugin", BackendRole.Notes);
		BackendCredentials credentials = new() { UserName = "user1@example.com", Password = "pass" };
		IBackendConnection connection = await provider.CreateConnectionAsync(
			new BackendConnectionContext
			{
				GatewayCredentials = credentials,
				GatewayUserId = 1,
				Roles =
				[
					new ResolvedRole
					{
						Role = BackendRole.Notes,
						ProviderName = "testplugin",
						Settings = ProviderSettings.Empty,
						Credentials = credentials
					}
				],
				SharedCollections = []
			},
			CancellationToken.None);

		// The connection is deliberately not disposed: the store IS the fixture's state, and the
		// caller runs the kit against it after this method returns. Nothing here holds an OS handle.
		return Assert.Single(connection.Stores);
	}

	/// <summary>
	///   A deliberately broken store: its revisions move on every enumeration although nothing
	///   changed, which would re-send every item to every device on every sync round.
	/// </summary>
	private sealed class UnstableRevisionStore : IContentStore
	{
		private static readonly FolderKey Folder = new("broken:notes");
		private int _reads;

		public bool OwnsKey(FolderKey key) => key == Folder;

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			Task.FromResult<IReadOnlyList<BackendFolder>>(
			[
				new BackendFolder { Key = Folder, DisplayName = "Broken", Type = FolderType.Notes }
			]);

		public Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
			FolderKey folder, ContentFilter filter, CancellationToken ct) =>
			Task.FromResult<IReadOnlyDictionary<ItemKey, ItemRevision>>(
				new Dictionary<ItemKey, ItemRevision>
				{
					[new ItemKey("always-there")] = new($"v{Interlocked.Increment(ref _reads)}")
				});

		public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct) =>
			Task.CompletedTask;

		public Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
			IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct) =>
			Task.FromResult<IReadOnlyList<FolderKey>>([]);
	}
}
