using ActiveSync.Contracts;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Backend;

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
