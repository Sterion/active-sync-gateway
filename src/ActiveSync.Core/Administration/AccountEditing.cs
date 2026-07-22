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
internal static class AccountEditing
{
	internal static AccountOptions Clone(AccountOptions source)
	{
		return JsonSerializer.Deserialize<AccountOptions>(
			JsonSerializer.Serialize(source, AccountStore.JsonOptions), AccountStore.JsonOptions)!;
	}

	/// <summary>DB entry, else a copy of the config entry, else a fresh one.</summary>
	internal static async Task<AccountOptions> LoadStartingEntryAsync(
		AccountStore store, ActiveSyncOptions options, string login, CancellationToken ct)
	{
		if (await store.GetAsync(login, ct).ConfigureAwait(false) is { } fromDb)
			return fromDb;
		return FindConfigUser(options, login) is { } fromConfig
			? Clone(fromConfig)
			: new AccountOptions();
	}

	/// <summary>
	///   Case-insensitive config-user lookup. <see cref="ActiveSyncOptions.Users" /> is bound by
	///   ConfigurationBinder with the default ORDINAL comparer, while logins are case-insensitive
	///   everywhere else — a differently-cased edit missed the config entry, started from an empty
	///   overlay and (a DB row replacing the whole config entry) discarded every override (B8).
	/// </summary>
	internal static AccountOptions? FindConfigUser(ActiveSyncOptions options, string login)
	{
		if (options.Users is null)
			return null;
		foreach ((string key, AccountOptions value) in options.Users)
			if (string.Equals(key, login, StringComparison.OrdinalIgnoreCase))
				return value;
		return null;
	}
}
