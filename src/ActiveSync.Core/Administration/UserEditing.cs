using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Administration;

/// <summary>
///   Shared edit semantics for declared-user entries (CLI and web API): edits start from the
///   existing entry — the database row, else a CLONE of the config entry (so the database
///   write becomes an exact replacement and the bound config object is never mutated), else a
///   fresh empty overlay.
/// </summary>
internal static class UserEditing
{
	internal static UserOptions Clone(UserOptions source)
	{
		return JsonSerializer.Deserialize<UserOptions>(
			JsonSerializer.Serialize(source, UserStore.JsonOptions), UserStore.JsonOptions)!;
	}

	/// <summary>DB entry, else a copy of the config entry, else a fresh one.</summary>
	internal static async Task<UserOptions> LoadStartingEntryAsync(
		UserStore store, ActiveSyncOptions options, string login, CancellationToken ct)
	{
		if (await store.GetAsync(login, ct).ConfigureAwait(false) is { } fromDb)
			return fromDb;
		return FindConfigUser(options, login) is { } fromConfig
			? Clone(fromConfig)
			: new UserOptions();
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
