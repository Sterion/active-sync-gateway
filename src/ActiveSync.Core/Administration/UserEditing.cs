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
}
