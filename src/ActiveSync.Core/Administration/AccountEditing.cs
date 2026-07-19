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
		return options.Users?.GetValueOrDefault(login) is { } fromConfig
			? Clone(fromConfig)
			: new AccountOptions();
	}
}
