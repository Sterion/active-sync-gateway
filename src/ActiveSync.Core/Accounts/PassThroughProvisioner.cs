using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   Just-in-time provisioning of pass-through users. When
///   <see cref="ActiveSyncOptions.AutoProvisionUsers" /> is on, the first time an undeclared
///   login clears its MailStore probe the gateway writes a bare <see cref="AccountEntry" /> row
///   for it (no gateway password, <see cref="AccountOptions.AutoProvisioned" /> set), so the user
///   turns into a first-class identity: visible in `eas users`/the admin UI, blockable, and
///   able to use the self-service portal (which only lets DECLARED accounts in, then verifies the
///   password against the same backend). The row is a pure overlay with nothing overridden, so it
///   changes no authentication or resolution behaviour — auth still probes the backend exactly as
///   pass-through did. Registered as a singleton and called from the EAS endpoint after a verified
///   login. Idempotent and best-effort: a failure is logged and swallowed so it can never break a
///   sync that already authenticated.
/// </summary>
public sealed class PassThroughProvisioner(
	AccountResolver resolver,
	AccountStore store,
	BackendProviderRegistry registry,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<PassThroughProvisioner> logger)
{
	/// <summary>
	///   Creates the row for <paramref name="login" /> if auto-provisioning is on and the login is
	///   not already declared (config or database). Cheap on the common path — a dictionary hit on
	///   the resolver's already-fresh snapshot — so it is safe to call on every authenticated
	///   request. Does nothing when the flag is off; never throws.
	/// </summary>
	public async Task ProvisionIfEnabledAsync(string login, CancellationToken ct)
	{
		if (!options.CurrentValue.AutoProvisionUsers)
			return;

		// The resolver is refreshed on the auth path already; MergedUsers therefore reflects both
		// config and database entries. A known login (declared, or provisioned on an earlier
		// request) is a no-op. RequireDeclaredUsers never coexists with a hit here — it rejects
		// undeclared logins before they authenticate, so we are never reached for one.
		if (resolver.MergedUsers.ContainsKey(login))
			return;

		AccountOptions entry = new() { AutoProvisioned = true };

		// Same config-grade validation every account write faces. An empty overlay only fails on a
		// malformed login (':'/control characters — which Basic auth cannot deliver anyway); skip
		// and warn rather than persist a row the resolver would later reject.
		List<string> failures = AccountResolver.ValidateEntry(
			options.CurrentValue, resolver.Roles, registry, login, entry);
		if (failures.Count > 0)
		{
			logger.LogWarning("Not auto-provisioning {User}: {Failures}", login, string.Join("; ", failures));
			return;
		}

		try
		{
			await store.UpsertAsync(login, entry, ct).ConfigureAwait(false);
			// Make the new row visible to this instance immediately so a burst of first-connect
			// requests from the same user does not each try to insert.
			await resolver.EnsureFreshAsync(true, ct).ConfigureAwait(false);
			logger.LogInformation("Auto-provisioned pass-through account {User} on first successful sign-in", login);
		}
		catch (DbUpdateException)
		{
			// Another replica or a concurrent device from the same user won the insert race (the
			// UserName index is unique). Benign — pick up their row and move on.
			await resolver.EnsureFreshAsync(true, ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Could not auto-provision pass-through account {User}", login);
		}
	}
}
