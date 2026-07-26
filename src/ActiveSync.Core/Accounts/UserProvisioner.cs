using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   Materializes the user IDENTITY at the auth boundary: after a login verifies, exactly one
///   <see cref="State.User" /> row exists for it and its immutable <c>UserId</c> is known —
///   every handler, store, notifier and cache past this point may assume both (no deferred
///   provisioning path exists, by design). Config-declared logins get an identity-only row
///   (configuration keeps supplying the values); undeclared logins get an
///   <see cref="UserOptions.AutoProvisioned" /> declaration when
///   <see cref="ActiveSyncOptions.AutoProvisionUsers" /> is on (visible in `eas users`, portal
///   sign-in allowed, blockable) — and with the flag OFF an undeclared login was already refused
///   before any backend probe (<see cref="UserResolver.VerifyLocally" />), so it never reaches
///   this. Registered as a singleton; called from every authenticated endpoint.
/// </summary>
public sealed class UserProvisioner(
	UserResolver resolver,
	UserStore store,
	Backend.BackendProviderRegistry registry,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<UserProvisioner> logger)
{
	/// <summary>
	///   The user id for an authenticated login, creating the row on first sign-in. Returns null
	///   only for a login that fails the structural login rules (':' / control characters — which
	///   Basic auth cannot ordinarily deliver); the caller must refuse the request then, because
	///   no identity exists for it. Cheap on the common path: one indexed point-read.
	/// </summary>
	public async Task<int?> EnsureUserAsync(string login, CancellationToken ct)
	{
		// A structurally invalid login never mints an identity — those characters would corrupt
		// session cache keys and log lines, and no legitimate client produces them.
		List<string> loginFailures = new();
		UserResolver.ValidateLogin(login, loginFailures);
		if (loginFailures.Count > 0)
		{
			logger.LogWarning("Refusing to provision structurally invalid login: {Failures}",
				string.Join("; ", loginFailures));
			return null;
		}

		// The resolver is refreshed on the auth path already; MergedUsers therefore reflects both
		// config and database entries.
		MergedUser? merged = resolver.MergedUsers.GetValueOrDefault(login);

		// An undeclared login that got this far authenticated via the backend probe with
		// AutoProvisionUsers on (off = VerifyLocally refused it pre-probe). It becomes a
		// first-class declared account, exactly like a hand-added empty entry plus the
		// provenance marker.
		UserOptions? declaration = null;
		if (merged is null && options.CurrentValue.AutoProvisionUsers)
		{
			UserOptions candidate = new() { AutoProvisioned = true };
			// Same config-grade validation every account write faces; an empty overlay only fails
			// on global-config edge cases — fall back to an identity-only row rather than refuse
			// a login that already authenticated.
			List<string> failures = UserResolver.ValidateEntry(
				options.CurrentValue, resolver.Roles, registry, login, candidate);
			if (failures.Count == 0)
				declaration = candidate;
			else
				logger.LogWarning("Not auto-declaring {User} (identity only): {Failures}",
					login, string.Join("; ", failures));
		}

		(int userId, bool declarationWritten) = await store.GetOrCreateUserAsync(login, declaration, ct)
			.ConfigureAwait(false);
		if (declarationWritten)
		{
			// Make the new declaration visible to this instance immediately so a burst of
			// first-connect requests from the same user does not each re-write it.
			await resolver.EnsureFreshAsync(true, ct).ConfigureAwait(false);
			logger.LogInformation(
				"Auto-provisioned pass-through account {User} on first successful sign-in", login);
		}

		return userId;
	}

	/// <summary>
	///   Gives every CONFIG-declared login an identity row at startup. Once every per-user table
	///   FKs to <c>UserId</c>, a config-declared user cannot stay config-only — its sync state
	///   would have nothing to point at. Configuration keeps supplying the VALUES; the row only
	///   supplies the IDENTITY, so it is created identity-only and never shadows the config entry.
	///   <para>
	///     Matching is by login, and that is safe precisely because a login is IMMUTABLE while it
	///     is config-declared (<c>eas user rename</c> refuses one): the database side can never
	///     drift from configuration, because the only mutable side is the one configuration does
	///     not own.
	///   </para>
	///   <para>
	///     The residual this CANNOT close: renaming the configuration KEY itself reads as a
	///     delete-plus-add to a gateway that does not own the file, so it creates a new user and
	///     strands the old row's data. Newly-created rows are therefore logged — cheap, and the
	///     only thing that surfaces it.
	///   </para>
	/// </summary>
	public async Task BootstrapConfigUsersAsync(CancellationToken ct)
	{
		Dictionary<string, UserOptions>? configUsers = options.CurrentValue.Users;
		if (configUsers is not { Count: > 0 })
			return;

		foreach (string login in configUsers.Keys)
		{
			List<string> failures = new();
			UserResolver.ValidateLogin(login, failures);
			if (failures.Count > 0)
				continue;   // startup validation already reports these

			try
			{
				bool existed = await store.FindUserIdAsync(login, ct).ConfigureAwait(false) is not null;
				(int userId, _) = await store.GetOrCreateUserAsync(login, null, ct).ConfigureAwait(false);
				if (existed)
					continue;

				logger.LogInformation(
					"Configuration declares {User} and no user had that login — created user {UserId}. " +
					"If you RENAMED a configuration key, the previous user's devices and locally-stored " +
					"items are still under the old login (the gateway cannot tell a rename from a " +
					"delete-plus-add in a file it does not own).",
					login, userId);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One unusable config login must not stop the gateway from starting.
				logger.LogWarning(ex, "Could not create the identity row for config-declared user {User}", login);
			}
		}
	}
}