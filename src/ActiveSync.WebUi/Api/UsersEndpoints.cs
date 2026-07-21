using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Declared-user management — the web face of `eas user`. Reads come from the resolver's
///   merged view (config ⊕ database, with provenance); writes go through the exact CLI
///   pipeline: clone-shadowed-config start (<see cref="AccountEditing" />), the shared secret
///   policy (<see cref="AccountSecretPolicy" />), config-grade validation
///   (<see cref="AccountResolver.ValidateEntry" />) and <see cref="AccountStore" />. Stored
///   passwords NEVER leave the server — DTOs carry set/unset flags; updates use a sentinel
///   (null = keep the stored value, "" = clear, anything else = set).
/// </summary>
internal static class UsersEndpoints
{
	internal sealed record RoleDto(
		bool? Enabled, string? Provider, string? UserName, bool PasswordSet,
		Dictionary<string, string?>? Settings);

	internal sealed record UserDto(
		string Login, string Origin, string? MailAddress, bool Admin, bool Enabled, bool PasswordSet,
		string? PasswordFormat, Dictionary<string, RoleDto>? Backends);

	internal sealed record RoleUpdate(
		bool? Enabled, string? Provider, string? UserName, string? Password,
		Dictionary<string, string?>? Settings);

	/// <summary>Full-replacement update; only the password fields are sentinel-merged.</summary>
	internal sealed record UserUpdateRequest(
		string? MailAddress, bool? Admin, bool? Enabled, string? Password, Dictionary<string, RoleUpdate>? Backends);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("users", async (AccountResolver resolver, CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			List<UserDto> users = resolver.MergedUsers
				.OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
				.Select(u => ToDto(u.Key, u.Value))
				.ToList();
			return Results.Ok(users);
		});

		api.MapGet("users/{login}", async (string login, AccountResolver resolver, CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			return resolver.MergedUsers.TryGetValue(login, out MergedAccount? account)
				? Results.Ok(ToDto(login, account))
				: Results.NotFound();
		});

		api.MapPut("users/{login}", async (
			string login, UserUpdateRequest request, ClaimsPrincipal principal, AccountStore store,
			AccountResolver resolver, BackendRolesConfig roles, BackendProviderRegistry registry,
			IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
		{
			ActiveSyncOptions current = options.CurrentValue;
			if (await LastAdminProblemAsync(
				    resolver, login, request.Admin == true && request.Enabled != false, ct) is { } conflict)
				return conflict;
			// Password sentinels merge against the entry a CLI edit would start from (the DB
			// row, else a clone of the config entry) so "keep" preserves the stored secret.
			AccountOptions starting = await AccountEditing.LoadStartingEntryAsync(store, current, login, ct);

			AccountOptions entry = new()
			{
				MailAddress = string.IsNullOrWhiteSpace(request.MailAddress) ? null : request.MailAddress.Trim(),
				Admin = request.Admin == true ? true : null,
				// Store the disabled flag only when explicitly off; enabled is the default (no flag).
				Enabled = request.Enabled == false ? false : null
			};

			string? gatewayPassword = MergeSecret(request.Password, starting.Password,
				raw => AccountSecretPolicy.PrepareGatewayPassword(raw), out string? passwordError);
			if (passwordError is not null)
				return Results.BadRequest(new { error = passwordError });
			entry.Password = gatewayPassword;

			if (request.Backends is { Count: > 0 })
			{
				entry.Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase);
				foreach ((string roleName, RoleUpdate role) in request.Backends)
				{
					string? storedRolePassword = starting.Backends?
						.FirstOrDefault(b => b.Key.Equals(roleName, StringComparison.OrdinalIgnoreCase))
						.Value?.Password;
					string? rolePassword = MergeSecret(role.Password, storedRolePassword,
						raw => AccountSecretPolicy.PrepareBackendPassword(
							raw, current.Encryption, $"Backends:{roleName}:Password"),
						out string? roleError);
					if (roleError is not null)
						return Results.BadRequest(new { error = roleError });

					BackendRoleOverride @override = new()
					{
						Enabled = role.Enabled,
						Provider = string.IsNullOrWhiteSpace(role.Provider) ? null : role.Provider,
						UserName = string.IsNullOrWhiteSpace(role.UserName) ? null : role.UserName,
						Password = rolePassword,
						Settings = role.Settings is { Count: > 0 }
							? new Dictionary<string, string?>(role.Settings, StringComparer.OrdinalIgnoreCase)
							: null
					};
					// An all-empty override carries no information — drop it instead of storing noise.
					if (@override is not
					    { Enabled: null, Provider: null, UserName: null, Password: null, Settings: null })
						entry.Backends[roleName] = @override;
				}

				if (entry.Backends.Count == 0)
					entry.Backends = null;
			}

			List<string> failures = AccountResolver.ValidateEntry(current, roles, registry, login, entry);
			if (failures.Count > 0)
				return Results.BadRequest(new { error = string.Join(Environment.NewLine, failures) });

			await store.UpsertAsync(login, entry, ct);
			await resolver.EnsureFreshAsync(true, ct);
			return Results.Ok(new
			{
				user = ToDto(login, new MergedAccount(entry, true, current.Users?.ContainsKey(login) == true)),
				warning = SelfEditWarning(principal, login, request.Admin == true, request.Enabled != false)
			});
		});

		api.MapDelete("users/{login}", async (
			string login, AccountStore store, AccountResolver resolver,
			IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
		{
			// Deleting the row can drop the last admin flag outright when no config entry sits
			// beneath it — the account itself goes away.
			ActiveSyncOptions configured = options.CurrentValue;
			bool staysAdmin = configured.Users?.TryGetValue(login, out AccountOptions? fallback) == true &&
			                  fallback!.Admin == true && fallback.Enabled != false;
			if (await LastAdminProblemAsync(resolver, login, staysAdmin, ct) is { } conflict)
				return conflict;

			bool removed = await store.DeleteAsync(login, ct);
			if (!removed)
				return Results.NotFound();
			await resolver.EnsureFreshAsync(true, ct);
			return Results.Ok(new
			{
				login,
				// The config entry (if any) is active again — the row only ever shadowed it.
				configFallback = options.CurrentValue.Users?.ContainsKey(login) == true
			});
		});

		// Quick disable/enable (parallel to devices block/unblock) — flips the account master
		// switch without a full-replacement PUT, so it can't clobber other fields.
		api.MapPost("users/{login}/disable", (string login, ClaimsPrincipal principal, AccountStore store,
				AccountResolver resolver, BackendRolesConfig roles, BackendProviderRegistry registry,
				IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
			SetEnabledAsync(login, false, principal, store, resolver, roles, registry, options, ct));

		api.MapPost("users/{login}/enable", (string login, ClaimsPrincipal principal, AccountStore store,
				AccountResolver resolver, BackendRolesConfig roles, BackendProviderRegistry registry,
				IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
			SetEnabledAsync(login, true, principal, store, resolver, roles, registry, options, ct));
	}

	/// <summary>
	///   A 409 when the write would leave the gateway with no enabled admin, otherwise null.
	///   <paramref name="staysAdmin" /> is what the target account will be AFTER the write.
	///   Recovery from zero admins is CLI-only — a legitimate escape hatch, but not one to walk
	///   into from a form with no warning.
	/// </summary>
	private static async Task<IResult?> LastAdminProblemAsync(
		AccountResolver resolver, string login, bool staysAdmin, CancellationToken ct)
	{
		if (staysAdmin)
			return null;
		await resolver.EnsureFreshAsync(false, ct);
		bool anotherRemains = resolver.MergedUsers.Any(pair =>
			!pair.Key.Equals(login, StringComparison.OrdinalIgnoreCase) &&
			pair.Value.Options.Admin == true &&
			pair.Value.Options.Enabled != false);
		return anotherRemains
			? null
			: Results.Conflict(new
			{
				error = $"'{login}' is the only enabled administrator — this would leave the web " +
				        "interface with no way in. Grant admin to another account first (or use " +
				        "the `eas user` CLI, which is the recovery path)."
			});
	}

	/// <summary>A note, not a refusal, when an admin edits their own account into a corner.</summary>
	private static string? SelfEditWarning(
		ClaimsPrincipal principal, string login, bool staysAdmin, bool staysEnabled)
	{
		if (!string.Equals(principal.Identity?.Name, login, StringComparison.OrdinalIgnoreCase))
			return null;
		if (!staysEnabled)
			return "You disabled your own account — this session ends within a minute.";
		return staysAdmin
			? null
			: "You removed your own administrator rights — this session drops to the user portal " +
			  "within a minute.";
	}

	/// <summary>Loads the entry a CLI edit would start from, flips its Enabled flag, validates and saves.</summary>
	private static async Task<IResult> SetEnabledAsync(
		string login, bool enable, ClaimsPrincipal principal, AccountStore store, AccountResolver resolver,
		BackendRolesConfig roles, BackendProviderRegistry registry,
		IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct)
	{
		ActiveSyncOptions current = options.CurrentValue;
		AccountOptions entry = await AccountEditing.LoadStartingEntryAsync(store, current, login, ct);
		if (await LastAdminProblemAsync(resolver, login, enable && entry.Admin == true, ct) is { } conflict)
			return conflict;
		entry.Enabled = enable ? null : false;
		List<string> failures = AccountResolver.ValidateEntry(current, roles, registry, login, entry);
		if (failures.Count > 0)
			return Results.BadRequest(new { error = string.Join(Environment.NewLine, failures) });
		await store.UpsertAsync(login, entry, ct);
		await resolver.EnsureFreshAsync(true, ct);
		return Results.Ok(new
		{
			user = ToDto(login, new MergedAccount(entry, true, current.Users?.ContainsKey(login) == true)),
			warning = SelfEditWarning(principal, login, entry.Admin == true, enable)
		});
	}

	/// <summary>null = keep the stored value, "" = clear, anything else = run the secret policy.</summary>
	private static string? MergeSecret(
		string? requested, string? stored,
		Func<string, AccountSecretPolicy.SecretResult> prepare, out string? error)
	{
		error = null;
		if (requested is null)
			return stored;
		if (requested.Length == 0)
			return null;
		AccountSecretPolicy.SecretResult result = prepare(requested);
		error = result.Error;
		return result.Value;
	}

	private static UserDto ToDto(string login, MergedAccount account)
	{
		AccountOptions o = account.Options;
		Dictionary<string, RoleDto>? backends = o.Backends is { Count: > 0 }
			? o.Backends.ToDictionary(
				b => b.Key,
				b => new RoleDto(
					b.Value.Enabled, b.Value.Provider, b.Value.UserName,
					!string.IsNullOrEmpty(b.Value.Password),
					b.Value.Settings is { Count: > 0 } ? b.Value.Settings : null),
				StringComparer.OrdinalIgnoreCase)
			: null;
		return new UserDto(
			login,
			account.FromDatabase
				? o.AutoProvisioned == true ? "db (auto)"
				: account.ShadowsConfig ? "db (shadows config)" : "db"
				: "config",
			o.MailAddress,
			o.Admin == true,
			o.Enabled != false,
			!string.IsNullOrEmpty(o.Password),
			string.IsNullOrEmpty(o.Password) ? null :
			GatewayPasswordHasher.IsHashed(o.Password) ? "pbkdf2" : "PLAINTEXT",
			backends);
	}
}
