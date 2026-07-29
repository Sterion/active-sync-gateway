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

		// The host contract version, read from the ActiveSync.Contracts assembly itself (pinned to
		// $(ContractVersion) in Directory.Build.props, never to the gateway release tag).
		Version hostContractVersion = ContractVersion.Current;
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
			VerifyDeclaredContract(entryDll, name, hostContractVersion);
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
			if (!IsPinningRequired(configuration))
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
	///   <c>ActiveSync:Plugins:RequirePinned</c> is read as a raw configuration string rather than
	///   through the options binder, so it must parse it itself rather than fail open on a value
	///   <see cref="bool.TryParse(string?, out bool)" /> cannot read. The natural env-var forms for
	///   the documented deployment (<c>ActiveSync__Plugins__RequirePinned=1</c>, or <c>yes</c>/
	///   <c>on</c>) are exactly the values <c>bool.TryParse</c> rejects — treating that as "not
	///   required" let an operator believe unpinned plugins were refused when they were not, with
	///   no log line and no startup error. A defaulting security gate must default to refusing, so
	///   an unparseable non-empty value is a startup failure instead.
	/// </summary>
	private static bool IsPinningRequired(IConfiguration configuration)
	{
		string? raw = configuration["ActiveSync:Plugins:RequirePinned"];
		if (string.IsNullOrEmpty(raw))
			return false;

		if (bool.TryParse(raw, out bool required))
			return required;

		throw new InvalidOperationException(
			$"ActiveSync:Plugins:RequirePinned must be true or false (got '{raw}').");
	}

	/// <summary>
	///   The digest a plugin directory is pinned by: SHA-256 over EVERY regular file beneath it
	///   (not just <c>*.dll</c>), ordered by relative path, hashing the path as well as the bytes
	///   so a renamed or added file changes the result. Public because it is the value an operator
	///   writes into <c>ActiveSync:Plugins:Pins:&lt;name&gt;</c> after reviewing a plugin — the
	///   loader also reports it in the mismatch message.
	///   <para>
	///     Covering every file matters because <c>*.dll</c> is not the whole loadable surface: on
	///     the shipped Linux image a plugin's P/Invoke payload is a <c>.so</c>/<c>.dylib</c>
	///     (<see cref="System.Runtime.Loader.AssemblyDependencyResolver" /> resolves those via
	///     <c>LoadUnmanagedDll</c>), and its <c>.deps.json</c> drives that same resolver's path
	///     lookups for both managed and native assets — an allow-list of one extension let either
	///     be swapped after review with the pin still matching.
	///   </para>
	/// </summary>
	public static string ComputeDirectoryDigest(string pluginDir)
	{
		using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (string file in Directory.EnumerateFiles(pluginDir, "*", SearchOption.AllDirectories)
			         .OrderBy(f => Path.GetRelativePath(pluginDir, f), StringComparer.Ordinal))
		{
			digest.AppendData(Encoding.UTF8.GetBytes(
				Path.GetRelativePath(pluginDir, file).Replace('\\', '/')));
			digest.AppendData([0]);
			using FileStream stream = File.OpenRead(file);
			digest.AppendData(SHA256.HashData(stream));
		}

		return Convert.ToHexStringLower(digest.GetHashAndReset());
	}

	/// <summary>
	///   The PRIMARY compatibility gate: the entry assembly must DECLARE the contract it supports
	///   via <see cref="SupportedGatewayContractAttribute" />, and it must match the host exactly
	///   on major and minor.
	///   <para>
	///     A declaration rather than an inference, because a plugin's own version and the contract
	///     package version it compiled against both say nothing about what its author actually
	///     verified. Read from METADATA so an incompatible assembly is refused before anything is
	///     loaded — the whole point being a comprehensible startup failure instead of a
	///     TypeLoadException deep inside a sync.
	///   </para>
	///   <para>
	///     An unreadable file is NOT rejected here: it falls through to the load, which fails with
	///     the loader's own "which plugin, and why" message rather than a misleading complaint
	///     about a missing attribute.
	///   </para>
	/// </summary>
	private static void VerifyDeclaredContract(string entryDll, string pluginName, Version hostContractVersion)
	{
		(int Major, int Minor)? declared = ReadDeclaredContract(entryDll, out bool readable);
		if (!readable)
			return;

		if (declared is null)
			throw new InvalidOperationException(
				$"Plugin '{pluginName}' does not declare which gateway contract it supports. Add " +
				$"[assembly: SupportedGatewayContract({hostContractVersion.Major}, {hostContractVersion.Minor})] " +
				$"to its entry assembly '{Path.GetFileName(entryDll)}'.");

		if (declared.Value.Major != hostContractVersion.Major || declared.Value.Minor != hostContractVersion.Minor)
			throw new InvalidOperationException(
				$"Plugin '{pluginName}' declares support for gateway contract " +
				$"{declared.Value.Major}.{declared.Value.Minor}, but this host implements " +
				$"{hostContractVersion.Major}.{hostContractVersion.Minor}. Every contract version is " +
				"breaking while the contract is pre-2.0; rebuild the plugin against this one.");
	}

	/// <summary>
	///   Reads the declared contract version out of the assembly's metadata without loading it.
	///   <paramref name="readable" /> is false when the file is not a readable managed image, which
	///   the caller treats as "not my problem" rather than as an undeclared plugin.
	/// </summary>
	private static (int Major, int Minor)? ReadDeclaredContract(string assemblyPath, out bool readable)
	{
		readable = true;
		try
		{
			using FileStream stream = File.OpenRead(assemblyPath);
			using PEReader pe = new(stream);
			if (!pe.HasMetadata)
			{
				readable = false;
				return null;
			}

			MetadataReader metadata = pe.GetMetadataReader();
			foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
			{
				CustomAttribute attribute = metadata.GetCustomAttribute(handle);
				if (!IsSupportedGatewayContract(metadata, attribute))
					continue;

				CustomAttributeValue<string> value = attribute.DecodeValue(AttributeTypeProvider.Instance);
				if (value.FixedArguments.Length >= 2
				    && value.FixedArguments[0].Value is int major
				    && value.FixedArguments[1].Value is int minor)
					return (major, minor);
			}
		}
		catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
		{
			readable = false;
		}

		return null;
	}

	/// <summary>
	///   Matches the attribute by type name alone: the plugin's reference to ActiveSync.Contracts
	///   is a TypeReference into whatever it resolved at ITS build time, so comparing resolution
	///   scopes would defeat the purpose of asking the plugin to declare a version at all.
	/// </summary>
	private static bool IsSupportedGatewayContract(MetadataReader metadata, CustomAttribute attribute)
	{
		if (attribute.Constructor.Kind != HandleKind.MemberReference)
			return false;

		MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
		if (constructor.Parent.Kind != HandleKind.TypeReference)
			return false;

		TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
		return metadata.GetString(type.Name) == nameof(SupportedGatewayContractAttribute)
		       && metadata.GetString(type.Namespace) == typeof(SupportedGatewayContractAttribute).Namespace;
	}

	/// <summary>
	///   Minimal type provider for <see cref="CustomAttribute.DecodeValue{TType}" />. The attribute
	///   carries two <see cref="int" /> arguments, so only primitives ever need naming; the rest
	///   satisfy the interface.
	/// </summary>
	private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
	{
		internal static readonly AttributeTypeProvider Instance = new();

		public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
		public string GetSystemType() => "System.Type";
		public string GetSZArrayType(string elementType) => elementType + "[]";
		public string GetTypeFromSerializedName(string name) => name;
		public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
		public bool IsSystemType(string type) => type == "System.Type";

		public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
			=> string.Empty;

		public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
			=> string.Empty;
	}

	/// <summary>
	///   Secondary guard, behind <see cref="VerifyDeclaredContract" />: no assembly in the plugin
	///   folder may REFERENCE a contract version other than the host's. This catches a bundle whose
	///   entry declares the right contract but which ships a private helper compiled against a
	///   different one — the helper's mismatched types would otherwise blow up deep inside a sync.
	///   Read from metadata over the whole folder, since a reference table only lists what an
	///   assembly itself uses.
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

			if (builtAgainst is not null
			    && (builtAgainst.Major != hostContractVersion.Major || builtAgainst.Minor != hostContractVersion.Minor))
				throw new InvalidOperationException(
					$"Plugin assembly '{Path.GetFileName(dll)}' was built against {contractName} " +
					$"{builtAgainst.Major}.{builtAgainst.Minor} but the host is {hostContractVersion}; " +
					"the major and minor versions must match.");
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
	///   One load context per plugin. Exactly one gateway assembly — <c>ActiveSync.Contracts</c> —
	///   plus the framework resolve from the DEFAULT context, so their types unify with the host's;
	///   everything else, gateway assemblies included, loads from the plugin folder when the plugin
	///   ships it.
	/// </summary>
	private sealed class PluginLoadContext(string entryDll) : AssemblyLoadContext(isCollectible: false)
	{
		/// <summary>Read from the assembly rather than written as a literal, so a rename cannot silently
		/// turn the one shared assembly into a plugin-local one.</summary>
		private static readonly string ContractAssemblyName = typeof(IGatewayPlugin).Assembly.GetName().Name!;

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
		///   The assemblies a plugin must share with the host: the plugin contract itself, plus the
		///   framework and the <c>Microsoft.Extensions.*</c> abstractions, because their types appear
		///   in the contract's own signatures (<c>IGatewayPlugin.Register</c> takes an
		///   <c>IServiceCollection</c> and an <c>IConfiguration</c>). A private copy of any of these
		///   would make <c>IBackendProvider</c> a different type and the provider would be ignored —
		///   which is why <c>docs/plugins.md</c> tells plugin authors not to ship them.
		///   <para>
		///     The contract match is EXACT on the simple name, never a prefix. A prefix would also
		///     capture <c>ActiveSync.Contracts.Interop</c> — an OPTIONAL package the gateway happens
		///     to ship its own copy of — and resolve the plugin's copy host-first. That is the
		///     silent-downgrade failure the <see cref="Load" /> comment describes, with a sharper
		///     edge: the host's interop copy binds the HOST's MimeKit/Ical.Net in the default
		///     context while the plugin's own code binds its private ones, so handing a
		///     plugin-context <c>MimeMessage</c> to a host-context extension method fails on type
		///     identity. A plugin using that package MUST ship it in its own folder, and this is
		///     what lets it.
		///   </para>
		///   <para>
		///     Narrowing to the one name also stops sharing Core, Crypto, Protocol and the backend
		///     assemblies, which a plugin has no business binding to the host's copy of: none of
		///     them appears in a contract signature any more, and they are not published.
		///   </para>
		/// </summary>
		private static bool IsHostOwned(AssemblyName assemblyName)
		{
			string name = assemblyName.Name ?? string.Empty;
			return string.Equals(name, ContractAssemblyName, StringComparison.Ordinal)
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
