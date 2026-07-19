using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   Holds the current <see cref="BackendRolesConfig" /> and rebuilds it when the
///   <c>ActiveSync:Backends</c> configuration subtree changes (a database settings edit flowing
///   through the config reload token). The initial load is strict (an invalid startup config
///   throws, as before); live rebuilds are LENIENT — an invalid edit is logged and the last good
///   configuration is kept, so a bad <c>eas config set</c> can never take a running (or multi-pod)
///   gateway down. Raises <see cref="Changed" /> only when the backend subtree actually changed,
///   so consumers (the resolver's snapshot, the session cache) recycle just for backend edits.
///   Registered as a singleton.
/// </summary>
public sealed class BackendRolesProvider : IDisposable
{
	private readonly IConfiguration _config;
	private readonly ILogger<BackendRolesProvider>? _logger;
	private readonly IDisposable? _reloadSubscription;
	private volatile BackendRolesConfig _current;
	private string _signature;

	public BackendRolesProvider(IConfiguration config, ILogger<BackendRolesProvider>? logger = null)
	{
		_config = config;
		_logger = logger;
		List<string> failures = new();
		BackendRolesConfig loaded = BackendRolesConfig.Load(config, failures);
		if (failures.Count > 0)
			throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
		_current = loaded;
		_signature = Signature(config);
		// Fires on any configuration reload (e.g. a database settings change); OnConfigReload
		// no-ops unless the ActiveSync:Backends subtree actually moved.
		_reloadSubscription = ChangeToken.OnChange(config.GetReloadToken, OnConfigReload);
	}

	/// <summary>The current role assignments. Read per use — never cache across a request.</summary>
	public BackendRolesConfig Current => _current;

	/// <summary>Raised after the backend role configuration was rebuilt from a change.</summary>
	public event Action? Changed;

	public void Dispose()
	{
		_reloadSubscription?.Dispose();
	}

	private void OnConfigReload()
	{
		string signature = Signature(_config);
		if (signature == _signature)
			return; // backends unchanged — a non-backend settings edit

		List<string> failures = new();
		BackendRolesConfig rebuilt = BackendRolesConfig.Load(_config, failures);
		if (failures.Count > 0)
		{
			_logger?.LogWarning(
				"Ignoring backend configuration change (invalid); keeping the current configuration: {Failures}",
				string.Join("; ", failures));
			return;
		}

		_signature = signature;
		_current = rebuilt;
		_logger?.LogInformation("Backend role configuration reloaded from a settings change");
		Changed?.Invoke();
	}

	/// <summary>Flattened, sorted ActiveSync:Backends subtree — changes exactly when a backend setting does.</summary>
	private static string Signature(IConfiguration config)
	{
		return string.Join("\n", config.GetSection("ActiveSync:Backends").AsEnumerable(true)
			.Where(pair => pair.Value is not null)
			.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => $"{pair.Key}={pair.Value}"));
	}
}
