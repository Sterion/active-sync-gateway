using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Backend;

/// <summary>
///   The functional slots a backend session must fill. Config assigns each role to a named
///   provider; one provider may serve several roles over a single connection (IMAP serves
///   MailStore, CalDAV serves Calendar+Tasks, a future JMAP provider could serve five).
/// </summary>
public enum BackendRole
{
	MailStore,
	MailSubmit,
	Calendar,
	Tasks,
	Contacts,
	Notes,
	Oof
}

/// <summary>
///   Effective settings of one role for one account (the global role section, overlaid with
///   the user's per-role settings). The host never binds these — the provider that owns the
///   role binds its OWN options type, which is what lets plugins carry option shapes the
///   host cannot know. The section never contains credentials; those travel separately as
///   <see cref="ResolvedRole.Credentials" />.
/// </summary>
public sealed class ProviderSettings(IConfigurationSection section)
{
	private static readonly IConfigurationSection EmptySection =
		new ConfigurationBuilder().Build().GetSection("empty");

	/// <summary>Settings with no keys at all (the "local" provider, absent sections).</summary>
	public static ProviderSettings Empty { get; } = new(EmptySection);

	public IConfigurationSection Section => section;

	/// <summary>Binds the section onto a fresh instance of the provider's options type.</summary>
	public TOptions Bind<TOptions>() where TOptions : new()
	{
		TOptions options = new();
		section.Bind(options);
		return options;
	}
}

/// <summary>
///   One role resolved for one account: which provider serves it, with what settings and
///   backend credentials.
/// </summary>
public sealed record ResolvedRole(
	BackendRole Role, string ProviderName, ProviderSettings Settings, BackendCredentials Credentials);

/// <summary>
///   Everything a provider needs to open one account's connection: the gateway identity
///   (DB scoping, encryption AAD, cache keys — never a backend login), the roles assigned
///   to THIS provider, and the shared-calendar grants. Host services (db factory, change
///   notifier, logging) reach providers through normal constructor injection instead.
/// </summary>
public sealed record BackendConnectionContext(
	BackendCredentials GatewayCredentials,
	string? MailAddress,
	IReadOnlyList<ResolvedRole> Roles,
	IReadOnlyList<SharedCollection> SharedCollections);

/// <summary>One provider's connection bundle for one account: its stores and side operations.</summary>
public interface IBackendConnection : IAsyncDisposable
{
	/// <summary>One content store per content role this connection fills.</summary>
	IReadOnlyList<IContentStore> Stores { get; }

	/// <summary>Set when the connection fills the MailSubmit role.</summary>
	IMailSubmitOperations? MailSubmit { get; }

	/// <summary>Set when the connection fills the Oof role.</summary>
	IOofBackend? Oof { get; }
}

/// <summary>Ready-made <see cref="IBackendConnection" /> that disposes its owned resources.</summary>
public sealed class BackendConnection(
	IReadOnlyList<IContentStore> stores,
	IMailSubmitOperations? mailSubmit = null,
	IOofBackend? oof = null,
	IReadOnlyList<object>? ownedResources = null) : IBackendConnection
{
	public IReadOnlyList<IContentStore> Stores => stores;
	public IMailSubmitOperations? MailSubmit => mailSubmit;
	public IOofBackend? Oof => oof;

	public async ValueTask DisposeAsync()
	{
		foreach (object resource in ownedResources ?? [])
			switch (resource)
			{
				case IAsyncDisposable asyncDisposable:
					await asyncDisposable.DisposeAsync().ConfigureAwait(false);
					break;
				case IDisposable disposable:
					disposable.Dispose();
					break;
			}
	}
}

/// <summary>
///   A named backend implementation ("imap", "caldav", "local", later "jmap") that can fill
///   one or more roles. Providers are process singletons — per-user caches (auth, watchers)
///   belong to the provider, per-account state belongs to the connections it creates.
/// </summary>
public interface IBackendProvider
{
	/// <summary>Unique name config refers to; compared case-insensitively.</summary>
	string Name { get; }

	IReadOnlySet<BackendRole> SupportedRoles { get; }

	/// <summary>One connection serving ALL roles assigned to this provider for one account.</summary>
	IBackendConnection CreateConnection(BackendConnectionContext context);

	/// <summary>
	///   Validates one effective role section (the provider binds its own options). Called for
	///   the global sections at startup (strict — failures abort) and for per-user overrides
	///   (config entries strict, database entries skip-with-warning).
	/// </summary>
	void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures);

	/// <summary>One redacted human-readable line for the startup banner (never secrets).</summary>
	string DescribeRole(BackendRole role, ProviderSettings settings);
}

/// <summary>
///   Optional provider capability: verifies presented credentials against the backend (the
///   HTTP Basic auth path when no local rule decides). Implementations return false on bad
///   credentials and throw <see cref="BackendException" /> when the backend is unreachable.
/// </summary>
public interface ICredentialVerifier
{
	Task<bool> VerifyCredentialsAsync(ResolvedRole role, CancellationToken ct);
}

/// <summary>
///   Optional provider capability: providers holding per-user caches (e.g. shared IMAP IDLE
///   watchers) trim them when the session factory's eviction sweep finds users without live
///   sessions. Called every sweep with the currently active gateway logins.
/// </summary>
public interface IPerUserResourceOwner
{
	/// <summary>Synchronous by design — disposal of trimmed resources happens in the background.</summary>
	void TrimUserResources(IReadOnlySet<string> activeGatewayLogins);
}

/// <summary>
///   Optional provider capability: a cheap reachability probe of the globally configured
///   endpoint for /readyz (no credentials — connectivity only, e.g. TCP banner or HTTP
///   OPTIONS). Providers without it simply do not appear in the readiness report.
/// </summary>
public interface IReadinessSource
{
	Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct);
}

/// <summary>All registered providers, addressable by name. Names must be unique.</summary>
public sealed class BackendProviderRegistry
{
	private readonly Dictionary<string, IBackendProvider> _providers;

	public BackendProviderRegistry(IEnumerable<IBackendProvider> providers, ILogger<BackendProviderRegistry> logger)
	{
		_providers = new Dictionary<string, IBackendProvider>(StringComparer.OrdinalIgnoreCase);
		foreach (IBackendProvider provider in providers)
		{
			if (!_providers.TryAdd(provider.Name, provider))
				throw new InvalidOperationException(
					$"Duplicate backend provider name '{provider.Name}' " +
					$"({_providers[provider.Name].GetType().FullName} vs {provider.GetType().FullName}).");
			logger.LogDebug("Registered backend provider {Provider} (roles: {Roles})",
				provider.Name, string.Join(", ", provider.SupportedRoles));
		}
	}

	public IReadOnlyCollection<IBackendProvider> All => _providers.Values;

	/// <summary>The provider registered under the given name, validated for the role.</summary>
	public IBackendProvider GetFor(string providerName, BackendRole role)
	{
		if (!_providers.TryGetValue(providerName, out IBackendProvider? provider))
			throw new InvalidOperationException(
				$"No backend provider named '{providerName}' is registered " +
				$"(available: {string.Join(", ", _providers.Keys.Order(StringComparer.OrdinalIgnoreCase))}).");
		if (!provider.SupportedRoles.Contains(role))
			throw new InvalidOperationException(
				$"Backend provider '{providerName}' does not support the {role} role " +
				$"(supported: {string.Join(", ", provider.SupportedRoles)}).");
		return provider;
	}
}
