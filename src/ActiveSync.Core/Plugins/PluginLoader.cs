using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Plugins;

/// <summary>
///   Discovers and loads out-of-repo backend plugins from a directory: one subdirectory per
///   plugin, its entry assembly named after the subdirectory (<c>my-notes/my-notes.dll</c>),
///   private dependencies beside it. Each plugin gets its own <see cref="AssemblyLoadContext" />
///   that resolves the shared contract (Core/Protocol/Backends.* and the framework) from the
///   HOST — so a plugin's <c>IBackendProvider</c> is the same type the registry indexes — and
///   falls back to its own folder for private dependencies.
///
///   Fails fast: a broken or incompatible plugin aborts startup rather than silently
///   degrading a role configured to use it to the local fallback (a data-visibility
///   incident). Absent/empty directory = no-op.
/// </summary>
public static class PluginLoader
{
	/// <summary>Default plugins directory relative to the app base (the image's /app/plugins).</summary>
	public const string DefaultDirectoryName = "plugins";

	/// <summary>
	///   Loads every plugin under the configured directory and lets each register its services.
	///   Called during service registration (before the provider is built), so plugin providers
	///   are present when <c>BackendProviderRegistry</c> is constructed.
	/// </summary>
	public static void LoadInto(IServiceCollection services, IConfiguration configuration, ILogger logger)
	{
		string directory = configuration["ActiveSync:Plugins:Directory"] is { Length: > 0 } configured
			? Path.GetFullPath(configured)
			: Path.Combine(AppContext.BaseDirectory, DefaultDirectoryName);

		if (!Directory.Exists(directory))
		{
			logger.LogDebug("No plugins directory at {Directory}; skipping plugin load", directory);
			return;
		}

		Version hostContractVersion = typeof(IGatewayPlugin).Assembly.GetName().Version ?? new Version(0, 0);
		int loaded = 0;
		foreach (string pluginDir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
		{
			string name = Path.GetFileName(pluginDir);
			string entryDll = Path.Combine(pluginDir, name + ".dll");
			if (!File.Exists(entryDll))
			{
				logger.LogWarning(
					"Plugin directory {Dir} has no entry assembly {Name}.dll; skipping", pluginDir, name);
				continue;
			}

			loaded += LoadPlugin(services, configuration, logger, entryDll, hostContractVersion);
		}

		if (loaded > 0)
			logger.LogInformation("Loaded {Count} gateway plugin(s) from {Directory}", loaded, directory);
	}

	private static int LoadPlugin(
		IServiceCollection services, IConfiguration configuration, ILogger logger,
		string entryDll, Version hostContractVersion)
	{
		PluginLoadContext context = new(entryDll);
		Assembly assembly;
		try
		{
			assembly = context.LoadFromAssemblyPath(entryDll);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException($"Failed to load plugin assembly '{entryDll}': {ex.Message}", ex);
		}

		// Compatibility guard: the major version of ActiveSync.Core the plugin was built against
		// must match the host — the backend contract is not ABI-stable across majors.
		AssemblyName? contractRef = assembly.GetReferencedAssemblies()
			.FirstOrDefault(a => a.Name == typeof(IGatewayPlugin).Assembly.GetName().Name);
		if (contractRef?.Version is { } builtAgainst && builtAgainst.Major != hostContractVersion.Major)
			throw new InvalidOperationException(
				$"Plugin '{Path.GetFileName(entryDll)}' was built against ActiveSync.Core " +
				$"{builtAgainst} but the host is {hostContractVersion}; major versions must match.");

		List<Type> pluginTypes = assembly.GetTypes()
			.Where(t => typeof(IGatewayPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
			.ToList();
		if (pluginTypes.Count == 0)
			throw new InvalidOperationException(
				$"Plugin assembly '{Path.GetFileName(entryDll)}' contains no public IGatewayPlugin implementation.");

		foreach (Type type in pluginTypes)
		{
			IGatewayPlugin plugin = Activator.CreateInstance(type) as IGatewayPlugin
				?? throw new InvalidOperationException(
					$"Plugin type {type.FullName} could not be instantiated (needs a public parameterless ctor).");
			plugin.Register(services, configuration);
			logger.LogInformation("Registered gateway plugin {Plugin} from {Assembly}",
				type.FullName, Path.GetFileName(entryDll));
		}

		return pluginTypes.Count;
	}

	/// <summary>
	///   One load context per plugin. The contract assemblies (Core/Protocol/Backends.* and
	///   the framework) resolve from the DEFAULT context so their types unify with the host's;
	///   only genuinely plugin-private dependencies load from the plugin folder.
	/// </summary>
	private sealed class PluginLoadContext(string entryDll) : AssemblyLoadContext(isCollectible: false)
	{
		private readonly AssemblyDependencyResolver _resolver = new(entryDll);

		protected override Assembly? Load(AssemblyName assemblyName)
		{
			// Host-first: if the default context already has (or can load) this assembly, use
			// that instance — critical for type identity of the shared contract.
			try
			{
				return Default.LoadFromAssemblyName(assemblyName);
			}
			catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
			{
				string? path = _resolver.ResolveAssemblyToPath(assemblyName);
				return path is null ? null : LoadFromAssemblyPath(path);
			}
		}

		protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
		{
			string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
			return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
		}
	}
}
