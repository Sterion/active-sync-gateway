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

	/// <summary>The fixture's private dependency, staged beside it and NOT in the base dir.</summary>
	private static readonly string StagedDepDll =
		Path.Combine(AppContext.BaseDirectory, "testplugin", "PluginPrivateLib.dll");

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
		PatchContractReferenceVersion(helper, 99);

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
		PatchContractReferenceVersion(Path.Combine(pluginDir, "ActiveSync.TestPlugin.dll"), 99);

		ServiceCollection services = new();
		services.AddLogging();
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			PluginLoader.LoadInto(services, ConfigFor(_root), NullLogger.Instance));
		Assert.Contains("major", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	///   K40 — <c>GetTypes()</c> on an untrusted assembly throws <see cref="ReflectionTypeLoadException" />
	///   whenever a type's base type or interface cannot be resolved, which is an ordinary consequence
	///   of a mis-packaged plugin. That escaped the loader raw, so startup died with a reflection
	///   exception naming nothing instead of the loader's own "which plugin, and why" message.
	///   Renaming the contract reference to an assembly that does not exist reproduces it: the
	///   file loads, and only resolving <c>IGatewayPlugin</c> fails.
	/// </summary>
	[Fact]
	public void AssemblyWhoseTypesCannotLoad_FailsFast_NamingThePlugin()
	{
		string pluginDir = StagePlugin("ActiveSync.TestPlugin");
		PatchContractReferenceName(Path.Combine(pluginDir, "ActiveSync.TestPlugin.dll"));

		ServiceCollection services = new();
		services.AddLogging();
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			PluginLoader.LoadInto(services, ConfigFor(_root), NullLogger.Instance));
		Assert.Contains("ActiveSync.TestPlugin.dll", ex.Message, StringComparison.Ordinal);
	}

	/// <summary>
	///   K40 — the loader's "contains no public IGatewayPlugin implementation" message promised a
	///   filter the code did not apply: a non-public entry point was instantiated and given the
	///   host's <see cref="IServiceCollection" /> just like a public one.
	/// </summary>
	[Fact]
	public void NonPublicPluginType_IsNotLoaded()
	{
		StagePlugin("ActiveSync.TestPlugin");

		BackendProviderRegistry registry = LoadAndBuildRegistry(ConfigFor(_root));

		Assert.Equal("testplugin", registry.GetFor("testplugin", BackendRole.Notes).Name);
		Assert.DoesNotContain(registry.All, p => p.Name == "internal-testplugin");
	}

	/// <summary>
	///   K41 — resolution was host-first for EVERY assembly, not just the shared contract, so any
	///   dependency the host happened to have loaded silently won over the copy the plugin shipped
	///   beside its entry assembly. A plugin pinned to its own build of a library therefore ran
	///   against the host's version instead — the classic silent downgrade. Only the contract and
	///   framework assemblies need to unify; everything else belongs to the plugin.
	/// </summary>
	[Fact]
	public void PluginPrivateDependency_LoadsFromThePluginFolder_NotTheHost()
	{
		string pluginDir = StagePlugin("ActiveSync.TestPlugin");
		File.Copy(StagedDepDll, Path.Combine(pluginDir, "PluginPrivateLib.dll"));

		BackendProviderRegistry registry = LoadAndBuildRegistry(ConfigFor(_root));

		// The provider reports where the copy of its private dependency it bound to came from.
		string described = registry.GetFor("testplugin", BackendRole.Notes)
			.DescribeRole(BackendRole.Notes, ProviderSettings.Empty);
		Assert.Contains(pluginDir, described, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	///   The other half of the same rule: the shared contract must still come from the HOST even
	///   when the plugin ships a copy, or its IBackendProvider is a different type and the registry
	///   never sees it. Guard against over-correcting K41 into plugin-first for everything.
	/// </summary>
	[Fact]
	public void SharedContract_StillResolvesFromTheHost_EvenWhenThePluginShipsACopy()
	{
		string pluginDir = StagePlugin("ActiveSync.TestPlugin");
		File.Copy(typeof(IGatewayPlugin).Assembly.Location,
			Path.Combine(pluginDir, "ActiveSync.Contracts.dll"));

		BackendProviderRegistry registry = LoadAndBuildRegistry(ConfigFor(_root));

		IBackendProvider provider = registry.GetFor("testplugin", BackendRole.Notes);
		Assert.Same(typeof(IBackendProvider), provider.GetType().GetInterface("IBackendProvider"));
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
	///   Rewrites the assembly's AssemblyRef row for ActiveSync.Contracts to claim another
	///   version — the metadata a plugin built against a different contract would carry. Patching
	///   the version bytes in place is what lets these tests exist at all: producing the same
	///   file honestly would mean shipping a second contract assembly to compile against.
	/// </summary>
	private static void PatchContractReferenceVersion(string assemblyPath, ushort major, ushort minor = 0)
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

		// AssemblyRef row: MajorVersion, MinorVersion, BuildNumber, RevisionNumber (4 x ushort).
		BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), major);
		BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 2), minor);
		File.WriteAllBytes(assemblyPath, image);
	}

	/// <summary>
	///   Rewrites the last character of the ActiveSync.Contracts reference name in the string heap
	///   (same length, so nothing downstream shifts) — the assembly then references a contract that
	///   cannot be found, which is what a mis-packaged plugin looks like to the loader.
	/// </summary>
	private static void PatchContractReferenceName(string assemblyPath)
	{
		byte[] image = File.ReadAllBytes(assemblyPath);
		int offset;
		using (MemoryStream stream = new(image, false))
		using (PEReader pe = new(stream))
		{
			MetadataReader metadata = pe.GetMetadataReader();
			AssemblyReference reference = metadata.AssemblyReferences
				.Select(handle => metadata.GetAssemblyReference(handle))
				.First(r => metadata.GetString(r.Name) == "ActiveSync.Contracts");
			offset = pe.PEHeaders.MetadataStartOffset
			         + metadata.GetHeapMetadataOffset(HeapIndex.String)
			         + MetadataTokens.GetHeapOffset(reference.Name)
			         + "ActiveSync.Contracts".Length - 1;
		}

		image[offset] = (byte)'z';
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
