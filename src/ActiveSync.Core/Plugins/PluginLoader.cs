using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using ActiveSync.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Plugins;

/// <summary>
///   Discovers and loads out-of-repo backend plugins from a directory: one subdirectory per
///   plugin, its entry assembly named after the subdirectory (<c>my-notes/my-notes.dll</c>),
///   private dependencies beside it. Each plugin gets its own <see cref="AssemblyLoadContext" />
///   that resolves the shared contract (Contracts/Core/Protocol/Backends.* and the framework) from the
///   HOST — so a plugin's <c>IBackendProvider</c> is the same type the registry indexes — and
///   falls back to its own folder for private dependencies.
///
///   Fails fast: a broken or incompatible plugin aborts startup rather than silently
///   degrading a role configured to use it to the local fallback (a data-visibility
///   incident). Absent/empty directory = no-op.
///
///   The load context is DEPENDENCY ISOLATION, not a security boundary: a plugin runs
///   in-process with the gateway's full rights (master key included) and is handed the live
///   <see cref="IServiceCollection" />, so it can replace host registrations. Installing one is
///   equivalent to installing a different build of the gateway. The only enforceable control is
///   refusing to load unreviewed bytes — hence <see cref="VerifyPin" /> and the host-controlled
///   (file/env only) plugin settings.
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
		// A relative path resolves against the APP BASE, the same root the default uses — not the
		// process working directory. Otherwise setting the option to its own documented default
		// ("plugins") would change which directory is scanned, and what the gateway loads into
		// itself would depend on where it was started from.
		string directory = configuration["ActiveSync:Plugins:Directory"] is { Length: > 0 } configured
			? Path.GetFullPath(configured, AppContext.BaseDirectory)
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

			// Dot-prefixed directories are by convention not plugins, and Kubernetes projected
			// volumes create exactly that (`..data`) beside the real content — the documented
			// volume-mount deployment would otherwise abort on every start.
			if (name.StartsWith('.'))
			{
				logger.LogDebug("Ignoring non-plugin directory {Dir}", pluginDir);
				continue;
			}

			// Fail fast, as documented: skipping here silently degrades whichever role config
			// assigned to this plugin to the local fallback, and the deployment still looks
			// healthy. A half-copied plugin directory is the common way to reach this.
			string entryDll = Path.Combine(pluginDir, name + ".dll");
			if (!File.Exists(entryDll))
				throw new InvalidOperationException(
					$"Plugin directory '{pluginDir}' has no entry assembly '{name}.dll'; the entry " +
					"assembly must be named after its directory.");

			VerifyPin(pluginDir, name, configuration, logger);
			VerifyContractVersions(pluginDir, hostContractVersion);
			loaded += LoadPlugin(services, configuration, logger, entryDll);
		}

		if (loaded > 0)
			logger.LogInformation("Loaded {Count} gateway plugin(s) from {Directory}", loaded, directory);
	}

	/// <summary>
	///   Optional integrity pinning. The load context isolates a plugin's *dependencies*, not its
	///   privileges — plugin code runs in-process with everything the gateway has, including the
	///   master key — so the only place that trust decision can be made is here, before any of it
	///   is loaded. An operator who reviews a plugin can pin its digest
	///   (<c>ActiveSync:Plugins:Pins:&lt;dirname&gt;</c>), and <c>ActiveSync:Plugins:RequirePinned</c>
	///   refuses anything unpinned. Both live in the host-controlled <c>Plugins</c> section, so they
	///   cannot be set from the database or the admin UI.
	/// </summary>
	private static void VerifyPin(string pluginDir, string name, IConfiguration configuration, ILogger logger)
	{
		string? pinned = configuration[$"ActiveSync:Plugins:Pins:{name}"];
		if (string.IsNullOrWhiteSpace(pinned))
		{
			if (!bool.TryParse(configuration["ActiveSync:Plugins:RequirePinned"], out bool required) || !required)
				return;

			throw new InvalidOperationException(
				$"Plugin '{name}' has no pinned digest and ActiveSync:Plugins:RequirePinned is set. " +
				$"Review the plugin and set ActiveSync:Plugins:Pins:{name} to " +
				$"'{ComputeDirectoryDigest(pluginDir)}'.");
		}

		string actual = ComputeDirectoryDigest(pluginDir);
		if (!string.Equals(actual, pinned.Trim(), StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException(
				$"Plugin '{name}' does not match its pinned digest: expected '{pinned.Trim()}', " +
				$"found '{actual}'. The plugin directory changed since it was pinned.");

		logger.LogDebug("Plugin {Name} matches its pinned digest", name);
	}

	/// <summary>
	///   The digest a plugin directory is pinned by: SHA-256 over every <c>*.dll</c> beneath it,
	///   ordered by relative path, hashing the path as well as the bytes so a renamed or added
	///   assembly changes the result. Public because it is the value an operator writes into
	///   <c>ActiveSync:Plugins:Pins:&lt;name&gt;</c> after reviewing a plugin — the loader also
	///   reports it in the mismatch message.
	/// </summary>
	public static string ComputeDirectoryDigest(string pluginDir)
	{
		using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (string dll in Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.AllDirectories)
			         .OrderBy(d => Path.GetRelativePath(pluginDir, d), StringComparer.Ordinal))
		{
			digest.AppendData(Encoding.UTF8.GetBytes(
				Path.GetRelativePath(pluginDir, dll).Replace('\\', '/')));
			digest.AppendData([0]);
			using FileStream stream = File.OpenRead(dll);
			digest.AppendData(SHA256.HashData(stream));
		}

		return Convert.ToHexStringLower(digest.GetHashAndReset());
	}

	/// <summary>
	///   Compatibility guard: the major version of ActiveSync.Contracts every assembly in the
	///   plugin folder was built against must match the host — the backend contract is not
	///   ABI-stable across majors. Read from METADATA, before anything is loaded, and over the
	///   whole folder rather than the entry assembly alone: a plugin whose entry targets the right
	///   major can still ship a private helper that does not, and a reference table only lists what
	///   an assembly itself uses. Getting that wrong turns a comprehensible startup failure into a
	///   TypeLoadException deep inside a sync.
	/// </summary>
	private static void VerifyContractVersions(string pluginDir, Version hostContractVersion)
	{
		string contractName = typeof(IGatewayPlugin).Assembly.GetName().Name!;
		foreach (string dll in Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.AllDirectories)
			         .OrderBy(d => d, StringComparer.Ordinal))
		{
			Version? builtAgainst;
			try
			{
				using FileStream stream = File.OpenRead(dll);
				using PEReader pe = new(stream);
				if (!pe.HasMetadata)
					continue;

				MetadataReader metadata = pe.GetMetadataReader();
				builtAgainst = metadata.AssemblyReferences
					.Select(handle => metadata.GetAssemblyReference(handle))
					.Where(reference => metadata.GetString(reference.Name) == contractName)
					.Select(reference => reference.Version)
					.FirstOrDefault();
			}
			catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
			{
				// Native library, resource-only file, or something unreadable: nothing to check
				// here. A genuinely corrupt entry assembly still fails when it is loaded.
				continue;
			}

			if (builtAgainst is not null && builtAgainst.Major != hostContractVersion.Major)
				throw new InvalidOperationException(
					$"Plugin assembly '{Path.GetFileName(dll)}' was built against {contractName} " +
					$"{builtAgainst} but the host is {hostContractVersion}; major versions must match.");
		}
	}

	private static int LoadPlugin(
		IServiceCollection services, IConfiguration configuration, ILogger logger, string entryDll)
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

		// GetTypes() throws as soon as ANY type's base type or interface cannot be resolved — an
		// everyday consequence of a mis-packaged plugin, not an exotic one. Left uncaught it kills
		// startup with a reflection exception that names no plugin; wrap it like every other
		// failure here so the operator learns which directory to look in.
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			string reason = ex.LoaderExceptions.FirstOrDefault(e => e is not null)?.Message ?? ex.Message;
			throw new InvalidOperationException(
				$"Plugin assembly '{Path.GetFileName(entryDll)}' could not be inspected — one or more of " +
				$"its types failed to load: {reason}", ex);
		}

		// IsPublic is the filter the "no public IGatewayPlugin implementation" message below has
		// always promised. Without it a plugin's non-public entry point was instantiated and handed
		// the host's service collection, so what the assembly chose to expose meant nothing.
		List<Type> pluginTypes = types
			.Where(t => t.IsPublic && typeof(IGatewayPlugin).IsAssignableFrom(t)
			                       && t is { IsAbstract: false, IsInterface: false })
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
	///   One load context per plugin. The contract assemblies (Contracts/Core/Protocol/Backends.* and
	///   the framework) resolve from the DEFAULT context so their types unify with the host's;
	///   only genuinely plugin-private dependencies load from the plugin folder.
	/// </summary>
	private sealed class PluginLoadContext(string entryDll) : AssemblyLoadContext(isCollectible: false)
	{
		private readonly AssemblyDependencyResolver _resolver = new(entryDll);
		private readonly string _pluginDir = Path.GetDirectoryName(entryDll)!;

		protected override Assembly? Load(AssemblyName assemblyName)
		{
			// Host-first ONLY for the assemblies whose types must be the host's — the contract
			// surface and the framework. Applying it to everything silently downgraded a plugin's
			// private dependency to whatever version the host happened to have loaded, which is
			// the opposite of what a per-plugin load context is for.
			if (IsHostOwned(assemblyName))
			{
				try
				{
					return Default.LoadFromAssemblyName(assemblyName);
				}
				catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
				{
					// Fall through to the plugin's own copy.
				}
			}

			string? path = _resolver.ResolveAssemblyToPath(assemblyName)
				?? ProbePluginFolder(assemblyName);
			// Null hands the assembly back to the runtime, which falls back to the default
			// context — so a host assembly the plugin does not ship still resolves.
			return path is null ? null : LoadFromAssemblyPath(path);
		}

		/// <summary>
		///   A plugin folder without a .deps.json (a hand-assembled drop rather than a
		///   <c>dotnet publish</c>) resolves nothing through <see cref="AssemblyDependencyResolver" />,
		///   so probe the folder by simple name as well. Both layouts are documented.
		/// </summary>
		private string? ProbePluginFolder(AssemblyName assemblyName)
		{
			if (assemblyName.Name is not { Length: > 0 } name)
				return null;

			string candidate = Path.Combine(_pluginDir, name + ".dll");
			return File.Exists(candidate) ? candidate : null;
		}

		/// <summary>
		///   The assemblies a plugin must share with the host: the plugin contract (and everything
		///   the gateway ships alongside it) plus the framework, because their types appear in the
		///   contract's own signatures. A private copy of any of these would make
		///   <c>IBackendProvider</c> a different type and the provider would be ignored — which is
		///   why <c>docs/plugins.md</c> tells plugin authors not to ship them.
		/// </summary>
		private static bool IsHostOwned(AssemblyName assemblyName)
		{
			string name = assemblyName.Name ?? string.Empty;
			return name.StartsWith("ActiveSync.", StringComparison.Ordinal)
			       || name.StartsWith("System.", StringComparison.Ordinal)
			       || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
			       || name is "System" or "mscorlib" or "netstandard";
		}

		protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
		{
			string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
			return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
		}
	}
}
