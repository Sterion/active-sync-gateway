using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Administration;

/// <summary>
///   Shared edit semantics for declared-user entries (CLI and web API): an edit starts from the
///   DATABASE DECLARATION ALONE — the existing row, else a fresh empty overlay — so what is
///   written back records only real DEVIATIONS.
///   <para>
///     It used to start from a CLONE of the config entry when no row existed, which was correct
///     while a row REPLACED the whole config entry: the clone made the replacement faithful.
///     Under per-field resolution (item 4) that would be a trap — copying config into the
///     database freezes every config-supplied value as a database override, so a later
///     configuration change would silently stop reaching that user. Config keeps supplying
///     whatever the database does not say.
///   </para>
/// </summary>
internal static class UserEditing
{
	internal static UserOptions Clone(UserOptions source)
	{
		return JsonSerializer.Deserialize<UserOptions>(
			JsonSerializer.Serialize(source, UserStore.JsonOptions), UserStore.JsonOptions)!;
	}

	/// <summary>The database declaration, else a fresh empty one (never a copy of config).</summary>
	internal static async Task<UserOptions> LoadStartingEntryAsync(
		UserStore store, ActiveSyncOptions options, string login, CancellationToken ct)
	{
		return await store.GetAsync(login, ct).ConfigureAwait(false) ?? new UserOptions();
	}

	/// <summary>
	///   Case-insensitive config-user lookup. <see cref="ActiveSyncOptions.Users" /> is bound by
	///   ConfigurationBinder with the default ORDINAL comparer, while logins are case-insensitive
	///   everywhere else — a differently-cased edit missed the config entry, started from an empty
	///   overlay and (a DB row replacing the whole config entry) discarded every override (B8).
	/// </summary>
	internal static UserOptions? FindConfigUser(ActiveSyncOptions options, string login)
	{
		if (options.Users is null)
			return null;
		foreach ((string key, UserOptions value) in options.Users)
			if (string.Equals(key, login, StringComparison.OrdinalIgnoreCase))
				return value;
		return null;
	}

	/// <summary>Case-insensitive role lookup on a (config or database) entry's <c>Backends</c> dictionary.</summary>
	internal static BackendRoleOverride? FindRole(UserOptions? entry, string roleName)
	{
		if (entry?.Backends is null)
			return null;
		foreach ((string key, BackendRoleOverride value) in entry.Backends)
			if (string.Equals(key, roleName, StringComparison.OrdinalIgnoreCase))
				return value;
		return null;
	}

	/// <summary>
	///   C2: an incoming scalar value that exactly matches what configuration alone already
	///   supplies for the same field is elided (returned as null — "no database override") rather
	///   than written back. A full-replacement PUT is populated from the MERGED (config ⊕ database)
	///   view, so an untouched field submits the config value verbatim; storing it as-is would
	///   freeze that value in the database row, and a later configuration edit would silently stop
	///   reaching this user. Storing null instead leaves the field to keep resolving through
	///   configuration, exactly as an unset row already does. A value with no config counterpart
	///   to match — or one the caller deliberately changed — passes through unchanged, so a real
	///   deviation is still recorded.
	/// </summary>
	internal static string? ElideIfMatchesConfig(string? incoming, string? configValue) =>
		incoming is not null && configValue is not null &&
		string.Equals(incoming, configValue, StringComparison.Ordinal)
			? null
			: incoming;

	/// <summary>The <see cref="bool" /> counterpart of <see cref="ElideIfMatchesConfig(string?, string?)" />.</summary>
	internal static bool? ElideIfMatchesConfig(bool? incoming, bool? configValue) =>
		incoming is not null && configValue is not null && incoming == configValue
			? null
			: incoming;

	/// <summary>
	///   The per-key <c>Settings</c> counterpart: drops a key whose incoming value equals the
	///   configuration-level value for the same key (so it stops being frozen as a database
	///   override), keeping everything else — including an explicit null "clear the inherited
	///   global key" directive, which by construction never equals a present config value.
	/// </summary>
	internal static Dictionary<string, string?>? ElideSettingsMatchingConfig(
		Dictionary<string, string?>? incoming, Dictionary<string, string?>? configSettings)
	{
		if (incoming is not { Count: > 0 })
			return incoming;
		Dictionary<string, string?> result = new(incoming, StringComparer.OrdinalIgnoreCase);
		if (configSettings is { Count: > 0 })
			foreach ((string key, string? value) in incoming)
				if (value is not null &&
				    configSettings.TryGetValue(key, out string? configValue) &&
				    string.Equals(value, configValue, StringComparison.Ordinal))
					result.Remove(key);
		return result.Count > 0 ? result : null;
	}
}
