using ActiveSync.Core.Administration;
using Microsoft.Extensions.Configuration;

namespace ActiveSync.Core.Settings;

/// <summary>
///   Configuration source exposing database-stored <see cref="ActiveSync.Core.State.GlobalSetting" />
///   rows as configuration keys. Added LAST in the configuration pipeline so database values win
///   over appsettings/env, which in turn win over the code (POCO) defaults. The provider holds an
///   in-memory snapshot; <see cref="DbSettingsConfigurationProvider.SetData" /> swaps it and fires
///   the reload token, which flows through <c>ConfigurationChangeTokenSource</c> into
///   <c>IOptionsMonitor</c>. The provider does no database I/O itself — the build-time initial load
///   (<see cref="DbSettingsLoader" />) and ongoing polling (<see cref="SettingsRefresher" />) push
///   snapshots into it, so it never needs the bootstrap connection string.
/// </summary>
public sealed class DbSettingsConfigurationSource : IConfigurationSource
{
	/// <summary>The provider built for this source (usable after the source is added to a builder).</summary>
	public DbSettingsConfigurationProvider Provider { get; } = new();

	public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

/// <summary>In-memory configuration provider whose snapshot is pushed in from the database.</summary>
public sealed class DbSettingsConfigurationProvider : ConfigurationProvider
{
	/// <summary>
	///   Replaces the settings snapshot and fires the reload token (IOptionsMonitor recomputes).
	///   Host-controlled keys (see <c>SettingKeys.HostControlledReason</c>) are DROPPED here rather
	///   than merely refused by the write surfaces: this is the one place every database-sourced
	///   value passes through, so a row inserted around the write path — by a DBA, a restored
	///   dump, or SQL injection elsewhere — cannot repoint the plugin directory or the users file.
	/// </summary>
	public void SetData(IReadOnlyDictionary<string, string?> data)
	{
		Dictionary<string, string?> filtered = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string key, string? value) in data)
			if (SettingKeys.HostControlledReason(key) is null)
				filtered[key] = value;

		Data = filtered;
		OnReload();
	}
}
