using ActiveSync.Core.Backend;
using ActiveSync.Core.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The out-of-repo plugin loader: a fixture plugin assembly (built by the project, staged
///   under testplugin/) is loaded from a temp plugins directory, its provider registers and
///   is indexed by the host's registry (proving contract type identity across the load
///   context), and broken/absent inputs behave as specified.
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
	private static readonly string StagedPluginDll =
		Path.Combine(AppContext.BaseDirectory, "testplugin", "ActiveSync.TestPlugin.dll");

	private readonly string _root =
		Path.Combine(Path.GetTempPath(), $"as-plugins-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// A non-collectible load context keeps the DLL mapped for the process lifetime,
			// so the loaded plugin file stays locked; the OS reclaims the temp dir later.
			// Not a test failure.
		}
	}

	private BackendProviderRegistry LoadAndBuildRegistry(IConfiguration configuration)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton<BackendProviderRegistry>();
		PluginLoader.LoadInto(services, configuration, NullLogger.Instance);
		return services.BuildServiceProvider().GetRequiredService<BackendProviderRegistry>();
	}

	private IConfiguration ConfigFor(string directory)
	{
		return new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["ActiveSync:Plugins:Directory"] = directory })
			.Build();
	}

	[Fact]
	public void LoadsPlugin_RegistersProvider_WithSharedContractIdentity()
	{
		Assert.True(File.Exists(StagedPluginDll),
			$"fixture plugin not staged at {StagedPluginDll} — check the StageTestPlugin build target");
		// Convention: entry assembly is <dir>.dll, so the directory is named after the dll.
		string pluginDir = Path.Combine(_root, "ActiveSync.TestPlugin");
		Directory.CreateDirectory(pluginDir);
		File.Copy(StagedPluginDll, Path.Combine(pluginDir, "ActiveSync.TestPlugin.dll"));

		BackendProviderRegistry registry = LoadAndBuildRegistry(ConfigFor(_root));

		// The provider was registered AND the registry (a host type) indexed it under the
		// Notes role — only possible if the plugin's IBackendProvider IS the host's type.
		IBackendProvider provider = registry.GetFor("testplugin", BackendRole.Notes);
		Assert.Equal("testplugin", provider.Name);
		Assert.NotSame(typeof(PluginLoaderTests).Assembly, provider.GetType().Assembly);
	}

	[Fact]
	public void MissingDirectory_IsANoOp()
	{
		BackendProviderRegistry registry = LoadAndBuildRegistry(ConfigFor(Path.Combine(_root, "does-not-exist")));
		Assert.Empty(registry.All);
	}

	[Fact]
	public void SubdirWithoutEntryAssembly_IsSkipped_NotFatal()
	{
		Directory.CreateDirectory(Path.Combine(_root, "empty-plugin"));
		BackendProviderRegistry registry = LoadAndBuildRegistry(ConfigFor(_root));
		Assert.Empty(registry.All);
	}

	[Fact]
	public void CorruptAssembly_FailsFast()
	{
		string pluginDir = Path.Combine(_root, "broken");
		Directory.CreateDirectory(pluginDir);
		File.WriteAllText(Path.Combine(pluginDir, "broken.dll"), "this is not a PE image");

		ServiceCollection services = new();
		services.AddLogging();
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			PluginLoader.LoadInto(services, ConfigFor(_root), NullLogger.Instance));
		Assert.Contains("broken.dll", ex.Message);
	}
}
