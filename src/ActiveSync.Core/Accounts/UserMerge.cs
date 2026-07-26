using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   Where an effective value came from. The levels below the user (global database, global
///   configuration, code default) are resolved by <c>IConfiguration</c> itself — the database
///   settings provider is layered last, so the DB wins over appsettings/env, which win over the
///   POCO defaults — so this enum names only the two levels the USER merge adds on top.
/// </summary>
public enum UserFieldSource
{
	/// <summary>Level 1: written by an admin (`eas user set`, admin API) or the holder (portal).</summary>
	UserDatabase,

	/// <summary>Level 2: the operator's <c>ActiveSync:Users:&lt;login&gt;</c> entry.</summary>
	UserConfig,
}

/// <summary>
///   THE RESOLUTION RULE, user half (docs/design/db-restructure.md § *THE RESOLUTION RULE*):
///   <c>user (DB) → user (config) → global (DB) → global (config) → code default</c>, resolved
///   PER FIELD — never per entry.
///   <para>
///     This replaces whole-entry replacement, where one database field silently discarded every
///     config-set field for that login. An override is a DEVIATION now: unset means "not set at
///     this level" and falls through, so clearing a user value reverts to configuration rather
///     than to nothing.
///   </para>
///   <para>
///     The one deliberate exception is inside a role's <c>Settings</c>, which is merged by KEY
///     PRESENCE rather than by non-null value. A null there is not "unset" — it is the existing
///     explicit-clear directive meaning "remove the inherited GLOBAL key", so a database null
///     must win over a config value instead of falling through to it. Both semantics therefore
///     survive being pushed through the extra level.
///   </para>
/// </summary>
public static class UserMerge
{
	/// <summary>The effective entry plus, per field path, which level supplied it.</summary>
	public sealed record Merged(UserOptions Options, IReadOnlyDictionary<string, UserFieldSource> Sources);

	/// <summary>
	///   Merges a database declaration over a configuration one. Either may be null (a user
	///   declared in only one place); with both null the result is an empty entry.
	/// </summary>
	public static Merged Merge(UserOptions? config, UserOptions? database)
	{
		Dictionary<string, UserFieldSource> sources = new(StringComparer.OrdinalIgnoreCase);
		UserOptions result = new();

		Take("Password", config?.Password, database?.Password, v => result.Password = v);
		Take("DefaultBackendLogin", config?.DefaultBackendLogin, database?.DefaultBackendLogin,
			v => result.DefaultBackendLogin = v);
		Take("DefaultBackendPassword", config?.DefaultBackendPassword, database?.DefaultBackendPassword,
			v => result.DefaultBackendPassword = v);
		Take("MailAddress", config?.MailAddress, database?.MailAddress, v => result.MailAddress = v);
		Take("Admin", config?.Admin, database?.Admin, v => result.Admin = v);
		Take("Enabled", config?.Enabled, database?.Enabled, v => result.Enabled = v);
		Take("OidcSubject", config?.OidcSubject, database?.OidcSubject, v => result.OidcSubject = v);
		// Provenance only: a marker on the row the gateway wrote itself, never config-supplied.
		Take("AutoProvisioned", config?.AutoProvisioned, database?.AutoProvisioned,
			v => result.AutoProvisioned = v);

		Dictionary<string, BackendRoleOverride> roles = MergeRoles(config?.Backends, database?.Backends, sources);
		if (roles.Count > 0)
			result.Backends = roles;

		return new Merged(result, sources);

		void Take<T>(string path, T? configValue, T? databaseValue, Action<T?> assign)
		{
			if (databaseValue is not null)
			{
				assign(databaseValue);
				sources[path] = UserFieldSource.UserDatabase;
			}
			else if (configValue is not null)
			{
				assign(configValue);
				sources[path] = UserFieldSource.UserConfig;
			}
		}
	}

	private static Dictionary<string, BackendRoleOverride> MergeRoles(
		Dictionary<string, BackendRoleOverride>? config,
		Dictionary<string, BackendRoleOverride>? database,
		Dictionary<string, UserFieldSource> sources)
	{
		Dictionary<string, BackendRoleOverride> merged = new(StringComparer.OrdinalIgnoreCase);
		IEnumerable<string> roleNames = (config?.Keys ?? Enumerable.Empty<string>())
			.Concat(database?.Keys ?? Enumerable.Empty<string>())
			.Distinct(StringComparer.OrdinalIgnoreCase);

		foreach (string role in roleNames)
		{
			BackendRoleOverride? configRole = Lookup(config, role);
			BackendRoleOverride? databaseRole = Lookup(database, role);
			BackendRoleOverride result = new();

			TakeRole($"Backends:{role}:Enabled", configRole?.Enabled, databaseRole?.Enabled,
				v => result.Enabled = v);
			TakeRole($"Backends:{role}:Provider", configRole?.Provider, databaseRole?.Provider,
				v => result.Provider = v);
			TakeRole($"Backends:{role}:UserName", configRole?.UserName, databaseRole?.UserName,
				v => result.UserName = v);
			TakeRole($"Backends:{role}:Password", configRole?.Password, databaseRole?.Password,
				v => result.Password = v);

			Dictionary<string, string?>? settings =
				MergeSettings(role, configRole?.Settings, databaseRole?.Settings, sources);
			if (settings is { Count: > 0 })
				result.Settings = settings;

			merged[role] = result;
		}

		return merged;

		void TakeRole<T>(string path, T? configValue, T? databaseValue, Action<T?> assign)
		{
			if (databaseValue is not null)
			{
				assign(databaseValue);
				sources[path] = UserFieldSource.UserDatabase;
			}
			else if (configValue is not null)
			{
				assign(configValue);
				sources[path] = UserFieldSource.UserConfig;
			}
		}
	}

	/// <summary>
	///   Merges one role's settings PER KEY, by presence rather than by value — a database key
	///   whose value is null is an explicit "clear the inherited global key" directive, so it
	///   must win over a config value for the same key rather than falling through to it.
	///   <para>
	///     List replacement rides along: because a list is addressed as <c>X:0</c>, <c>X:1</c>, …,
	///     a database level that sets ANY element of a list replaces the config level's whole
	///     list — otherwise a shorter database list would silently inherit the config list's
	///     trailing elements. This mirrors what <c>UserResolver.MergeSettings</c> already does
	///     when overlaying a user's settings onto the global role section.
	///   </para>
	/// </summary>
	private static Dictionary<string, string?>? MergeSettings(
		string role,
		Dictionary<string, string?>? config,
		Dictionary<string, string?>? database,
		Dictionary<string, UserFieldSource> sources)
	{
		if (database is not { Count: > 0 })
		{
			if (config is not { Count: > 0 })
				return null;
			foreach (string key in config.Keys)
				sources[$"Backends:{role}:Settings:{key}"] = UserFieldSource.UserConfig;
			return new Dictionary<string, string?>(config, StringComparer.OrdinalIgnoreCase);
		}

		Dictionary<string, string?> merged = new(StringComparer.OrdinalIgnoreCase);
		if (config is { Count: > 0 })
		{
			// Drop the config list roots the database level touches, so a shorter database list
			// cannot inherit trailing config elements.
			HashSet<string> replacedRoots = new(StringComparer.OrdinalIgnoreCase);
			foreach (string databaseKey in database.Keys)
				replacedRoots.Add(BackendConfigValidation.ListRoot(databaseKey));

			foreach ((string key, string? value) in config)
			{
				string root = BackendConfigValidation.ListRoot(key);
				if (replacedRoots.Contains(root))
					continue;
				merged[key] = value;
				sources[$"Backends:{role}:Settings:{key}"] = UserFieldSource.UserConfig;
			}
		}

		foreach ((string key, string? value) in database)
		{
			merged[key] = value;
			sources[$"Backends:{role}:Settings:{key}"] = UserFieldSource.UserDatabase;
		}

		return merged;
	}

	private static BackendRoleOverride? Lookup(Dictionary<string, BackendRoleOverride>? roles, string role)
	{
		if (roles is null)
			return null;
		// Config binds Users with the ORDINAL comparer, so a case-differing role name would miss
		// (the same B8 shape the login lookups had).
		if (roles.TryGetValue(role, out BackendRoleOverride? exact))
			return exact;
		foreach ((string name, BackendRoleOverride value) in roles)
			if (name.Equals(role, StringComparison.OrdinalIgnoreCase))
				return value;
		return null;
	}
}
