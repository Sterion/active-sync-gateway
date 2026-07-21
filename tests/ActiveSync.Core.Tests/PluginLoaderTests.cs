using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ActiveSync.Contracts;
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

	/// <summary>
	///   K39 — the contract-major guard used to read the ENTRY assembly's reference table only, so
	///   a plugin whose entry was built against the right major could ship a private helper built
	///   against another one. The helper's mismatched types then blow up deep inside a sync with a
	///   TypeLoadException instead of being refused at startup with a comprehensible message.
	/// </summary>
	[Fact]
	public void ContractMajorMismatch_InAPrivateDependency_FailsFast()
	{
		string pluginDir = StagePlugin("ActiveSync.TestPlugin");
		string helper = Path.Combine(pluginDir, "PrivateHelper.dll");
		File.Copy(StagedPluginDll, helper);
		PatchContractReferenceMajor(helper, 99);

		ServiceCollection services = new();
		services.AddLogging();
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			PluginLoader.LoadInto(services, ConfigFor(_root), NullLogger.Instance));
		Assert.Contains("PrivateHelper.dll", ex.Message, StringComparison.Ordinal);
		Assert.Contains("major", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>The same guard on the entry assembly itself — the case that already worked.</summary>
	[Fact]
	public void ContractMajorMismatch_InTheEntryAssembly_FailsFast()
	{
		string pluginDir = StagePlugin("ActiveSync.TestPlugin");
		PatchContractReferenceMajor(Path.Combine(pluginDir, "ActiveSync.TestPlugin.dll"), 99);

		ServiceCollection services = new();
		services.AddLogging();
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			PluginLoader.LoadInto(services, ConfigFor(_root), NullLogger.Instance));
		Assert.Contains("major", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Copies the fixture plugin into a correctly-named subdirectory and returns it.</summary>
	private string StagePlugin(string name)
	{
		string pluginDir = Path.Combine(_root, name);
		Directory.CreateDirectory(pluginDir);
		File.Copy(StagedPluginDll, Path.Combine(pluginDir, name + ".dll"));
		return pluginDir;
	}

	/// <summary>
	///   Rewrites the assembly's AssemblyRef row for ActiveSync.Contracts to claim another major
	///   version — the metadata a plugin built against a different contract would carry. Patching
	///   the two version bytes in place is what lets these tests exist at all: producing the same
	///   file honestly would mean shipping a second contract assembly to compile against.
	/// </summary>
	private static void PatchContractReferenceMajor(string assemblyPath, ushort major)
	{
		byte[] image = File.ReadAllBytes(assemblyPath);
		int offset;
		using (MemoryStream stream = new(image, false))
		using (PEReader pe = new(stream))
		{
			MetadataReader metadata = pe.GetMetadataReader();
			AssemblyReferenceHandle handle = metadata.AssemblyReferences.First(h =>
				metadata.GetString(metadata.GetAssemblyReference(h).Name) == "ActiveSync.Contracts");
			// AssemblyRef row layout starts with MajorVersion as a little-endian ushort.
			offset = pe.PEHeaders.MetadataStartOffset
			         + metadata.GetTableMetadataOffset(TableIndex.AssemblyRef)
			         + (MetadataTokens.GetRowNumber(handle) - 1) * metadata.GetTableRowSize(TableIndex.AssemblyRef);
		}

		BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), major);
		File.WriteAllBytes(assemblyPath, image);
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
