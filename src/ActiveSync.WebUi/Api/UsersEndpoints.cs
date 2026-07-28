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
///   pipeline: clone-shadowed-config start (<see cref="UserEditing" />), the shared secret
///   policy (<see cref="UserSecretPolicy" />), config-grade validation
///   (<see cref="UserResolver.ValidateEntry" />) and <see cref="UserStore" />. Stored
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
		string? PasswordFormat, Dictionary<string, RoleDto>? Backends,
		Dictionary<string, string>? Sources);

	internal sealed record RoleUpdate(
		bool? Enabled, string? Provider, string? UserName, string? Password,
		Dictionary<string, string?>? Settings);

	/// <summary>Full-replacement update; only the password fields are sentinel-merged.</summary>
	internal sealed record UserUpdateRequest(
		string? MailAddress, bool? Admin, bool? Enabled, string? Password, Dictionary<string, RoleUpdate>? Backends);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("users", async (UserResolver resolver, CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			List<UserDto> users = resolver.MergedUsers
				.OrderBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
				.Select(u => ToDto(u.Key, u.Value))
				.ToList();
			return Results.Ok(users);
		});

		api.MapGet("users/{login}", async (string login, UserResolver resolver, CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			return resolver.MergedUsers.TryGetValue(login, out MergedUser? account)
				? Results.Ok(ToDto(login, account))
				: Results.NotFound();
		});

		api.MapPut("users/{login}", async (
			string login, UserUpdateRequest request, ClaimsPrincipal principal, UserStore store,
			UserResolver resolver, BackendRolesConfig roles, BackendProviderRegistry registry,
			IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
		{
			ActiveSyncOptions current = options.CurrentValue;
			if (await LastAdminProblemAsync(
				    resolver, login, request.Admin == true && request.Enabled != false, ct) is { } conflict)
				return conflict;

			UserOptions? configUser = UserEditing.FindConfigUser(current, login);

			// Fields this DTO does not model are CARRIED FORWARD from the database row, not dropped:
			// the PUT replaces the row wholesale, so anything the admin screen cannot see would
			// otherwise be silently destroyed by an unrelated edit — a stored DefaultBackendPassword
			// (breaking every backend for that user), the OIDC subject binding that stops the login
			// being claimed by someone else, or the auto-provisioned provenance marker.
			UserOptions? storedRow = await store.GetAsync(login, ct);
			// Reveals what a re-posted "***" settings mask stands for: the GET that produced it
			// masked the MERGED (config over database) value, so unmasking must resolve against the
			// same merge — resolving against the database row alone would treat a config-only secret
			// as if nothing were behind the mask and silently drop it. This is ONLY for reading
			// through the mask; ElideSettingsMatchingConfig below still keeps an unmasked value that
			// turns out to equal configuration from being written back as a database override.
			UserOptions merged = UserMerge.Merge(configUser, storedRow).Options;
			UserOptions entry = new()
			{
				// The form is populated from the MERGED (config over database) view the GET
				// returned, so an admin who touches nothing resubmits the config value verbatim.
				// Storing it here would freeze it as a permanent database override — precisely what
				// per-field resolution is meant to avoid, since loading a starting entry used to
				// clone config into the row and that froze later config changes out — so a submitted
				// value equal to what configuration alone already supplies is elided back to null:
				// the field keeps resolving through configuration exactly as an unset row already
				// does. A value that genuinely differs still lands as a real deviation.
				MailAddress = UserEditing.ElideIfMatchesConfig(
					string.IsNullOrWhiteSpace(request.MailAddress) ? null : request.MailAddress.Trim(),
					configUser?.MailAddress),
				Admin = UserEditing.ElideIfMatchesConfig(request.Admin == true ? true : null, configUser?.Admin),
				// Store the disabled flag only when explicitly off; enabled is the default (no flag).
				Enabled = UserEditing.ElideIfMatchesConfig(
					request.Enabled == false ? false : null, configUser?.Enabled),
				DefaultBackendLogin = storedRow?.DefaultBackendLogin,
				DefaultBackendPassword = storedRow?.DefaultBackendPassword,
				OidcSubject = storedRow?.OidcSubject,
				AutoProvisioned = storedRow?.AutoProvisioned
			};

			// Secret sentinel ("keep what I was shown") resolves against the STORED ROW alone, not
			// the merged view: an unset row already falls back to configuration on its own, so
			// resolving "keep" against the row is enough to preserve the effective password.
			// Resolving it against the merge (the previous behaviour) copied a config-supplied
			// password into the row on every untouched save — the same config-value freeze the
			// fields above are elided to avoid, one field over.
			string? gatewayPassword = MergeSecret(request.Password, storedRow?.Password,
				raw => UserSecretPolicy.PrepareGatewayPassword(raw), out string? passwordError);
			if (passwordError is not null)
				return EndpointHelpers.BadRequest(passwordError);
			entry.Password = gatewayPassword;

			if (request.Backends is { Count: > 0 })
			{
				entry.Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase);
				foreach ((string roleName, RoleUpdate role) in request.Backends)
				{
					BackendRoleOverride? configRole = UserEditing.FindRole(configUser, roleName);
					BackendRoleOverride? storedRole = UserEditing.FindRole(storedRow, roleName);
					BackendRoleOverride? mergedRole = UserEditing.FindRole(merged, roleName);
					string? rolePassword = MergeSecret(role.Password, storedRole?.Password,
						raw => UserSecretPolicy.PrepareBackendPassword(
							raw, current.Encryption, $"Backends:{roleName}:Password"),
						out string? roleError);
					if (roleError is not null)
						return EndpointHelpers.BadRequest(roleError);

					BackendRoleOverride @override = new()
					{
						Enabled = UserEditing.ElideIfMatchesConfig(role.Enabled, configRole?.Enabled),
						Provider = UserEditing.ElideIfMatchesConfig(
							string.IsNullOrWhiteSpace(role.Provider) ? null : role.Provider, configRole?.Provider),
						UserName = UserEditing.ElideIfMatchesConfig(
							string.IsNullOrWhiteSpace(role.UserName) ? null : role.UserName, configRole?.UserName),
						Password = rolePassword,
						Settings = UserEditing.ElideSettingsMatchingConfig(
							EndpointHelpers.UnmaskSecretSettings(role.Settings, mergedRole?.Settings),
							configRole?.Settings)
					};
					// An all-empty override carries no information — drop it instead of storing noise.
					if (@override is not
					    { Enabled: null, Provider: null, UserName: null, Password: null, Settings: null })
						entry.Backends[roleName] = @override;
				}

				if (entry.Backends.Count == 0)
					entry.Backends = null;
			}

			List<string> failures = UserResolver.ValidateEntry(current, roles, registry, login, entry);
			if (failures.Count > 0)
				return EndpointHelpers.BadRequest(string.Join(Environment.NewLine, failures));

			await store.UpsertAsync(login, entry, ct);
			await resolver.EnsureFreshAsync(true, ct);
			return Results.Ok(new
			{
				user = ToDto(login, new MergedUser(entry, true, configUser is not null)),
				warning = SelfEditWarning(principal, login, request.Admin == true, request.Enabled != false)
			});
		});

		api.MapDelete("users/{login}", async (
			string login, UserStore store, UserResolver resolver,
			IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
		{
			// Deleting the row can drop the last admin flag outright when no config entry sits
			// beneath it — the account itself goes away.
			ActiveSyncOptions configured = options.CurrentValue;
			bool staysAdmin = configured.Users?.TryGetValue(login, out UserOptions? fallback) == true &&
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
				configFallback = UserEditing.FindConfigUser(options.CurrentValue, login) is not null
			});
		});

		// Quick disable/enable (parallel to devices block/unblock) — flips the account master
		// switch without a full-replacement PUT, so it can't clobber other fields.
		api.MapPost("users/{login}/disable", (string login, ClaimsPrincipal principal, UserStore store,
				UserResolver resolver, BackendRolesConfig roles, BackendProviderRegistry registry,
				IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
			SetEnabledAsync(login, false, principal, store, resolver, roles, registry, options, ct));

		api.MapPost("users/{login}/enable", (string login, ClaimsPrincipal principal, UserStore store,
				UserResolver resolver, BackendRolesConfig roles, BackendProviderRegistry registry,
				IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
			SetEnabledAsync(login, true, principal, store, resolver, roles, registry, options, ct));

		// Renaming a login is possible at all only because identity is a surrogate key: the
		// rename is a single-row update, so sync state and locally-stored items are unaffected.
		api.MapPost("users/{login}/rename", async (
			string login, RenameRequest request, UserStore store, UserResolver resolver,
			IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
		{
			if (AdminIdentifiers.LoginProblem(request.NewLogin) is { } loginError)
				return EndpointHelpers.BadRequest(loginError);
			string newLogin = request.NewLogin!.Trim();
			ActiveSyncOptions current = options.CurrentValue;

			// The SAME two guards the CLI applies — the UI must not be able to write a shape the
			// CLI refuses. A config-declared login is immutable, which is exactly what keeps
			// config↔database matching-by-login from ever drifting.
			if (UserEditing.FindConfigUser(current, login) is not null)
				return EndpointHelpers.BadRequest(
					$"'{login}' is declared in configuration — change it there (ActiveSync:Users). " +
					"A config-declared login is immutable so the two sides cannot drift.");
			if (UserEditing.FindConfigUser(current, newLogin) is not null)
				return EndpointHelpers.BadRequest(
					$"'{newLogin}' is already declared in configuration — pick a login that is free.");

			UserStore.RenameOutcome outcome = await store.RenameAsync(login, newLogin, ct);
			if (outcome == UserStore.RenameOutcome.UnknownUser)
				return Results.NotFound();
			if (outcome == UserStore.RenameOutcome.Collision)
				return EndpointHelpers.BadRequest($"'{newLogin}' is already taken by another user.");

			await resolver.EnsureFreshAsync(true, ct);
			return Results.Ok(new
			{
				login = newLogin,
				previousLogin = login,
				note = "Sync state, devices and locally-stored items are unaffected — the phone " +
				       "just needs its username updated.",
			});
		});

		// DELETE the user itself (distinct from `remove`, which only drops the database
		// declaration). Confirm-and-cascade: the database cascades, and this refuses to issue it
		// blind — GET the impact, then repost with the login echoed back.
		api.MapGet("users/{login}/deletion-impact", async (
			string login, DeviceAdminService devices, UserStore store, CancellationToken ct) =>
		{
			if (await store.FindUserIdAsync(login, ct) is null)
				return Results.NotFound();
			DeviceAdminService.DeletionImpact impact = await devices.CountDeletionImpactAsync(login, null, ct);
			return Results.Ok(new
			{
				login,
				destroysContent = impact.DestroysContent,
				content = impact.Content.Where(c => c.Count > 0)
					.Select(c => new { collection = c.Table, count = c.Count }),
				syncState = impact.SyncState.Where(c => c.Count > 0)
					.Select(c => new { table = c.Table, count = c.Count }),
				summary = impact.DescribeContent(),
			});
		});

		api.MapPost("users/{login}/delete", async (
			string login, DeleteUserRequest request, UserStore store, UserResolver resolver,
			DeviceAdminService devices, IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
		{
			if (await store.FindUserIdAsync(login, ct) is null)
				return Results.NotFound();

			// Unlike PUT/DELETE/disable, this cascade-deletes the account outright rather than
			// merely dropping a database override, so it needs the same last-admin guard: the
			// deleted account never "stays admin" afterward.
			if (await LastAdminProblemAsync(resolver, login, staysAdmin: false, ct) is { } conflict)
				return conflict;

			// Typed echo, the same idiom wipe/purge already use. Graduated: content-owning users
			// get the counts in the refusal so the dialog can name what is at stake.
			DeviceAdminService.DeletionImpact impact = await devices.CountDeletionImpactAsync(login, null, ct);
			if (!string.Equals(request.Confirm, login, StringComparison.Ordinal))
				return EndpointHelpers.BadRequest(impact.DestroysContent
					? $"confirm must echo '{login}' — this permanently destroys " +
					  $"{impact.DescribeContent()}, which exist nowhere else"
					: $"confirm must echo '{login}'");

			if (!await store.DeleteUserAsync(login, ct))
				return Results.NotFound();
			await resolver.EnsureFreshAsync(true, ct);
			return Results.Ok(new
			{
				login,
				deleted = true,
				destroyed = impact.DescribeContent(),
				// Configuration is not ours to edit: say so rather than let the row silently return.
				configFallback = UserEditing.FindConfigUser(options.CurrentValue, login) is not null,
			});
		});
	}

	internal sealed record RenameRequest(string? NewLogin);

	internal sealed record DeleteUserRequest(string? Confirm);

	/// <summary>
	///   A 409 when the write would leave the gateway with no enabled admin, otherwise null.
	///   <paramref name="staysAdmin" /> is what the target account will be AFTER the write.
	///   Recovery from zero admins is CLI-only — a legitimate escape hatch, but not one to walk
	///   into from a form with no warning.
	/// </summary>
	private static async Task<IResult?> LastAdminProblemAsync(
		UserResolver resolver, string login, bool staysAdmin, CancellationToken ct)
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
		string login, bool enable, ClaimsPrincipal principal, UserStore store, UserResolver resolver,
		BackendRolesConfig roles, BackendProviderRegistry registry,
		IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct)
	{
		ActiveSyncOptions current = options.CurrentValue;
		UserOptions entry = await UserEditing.LoadStartingEntryAsync(store, login, ct);
		if (await LastAdminProblemAsync(resolver, login, enable && entry.Admin == true, ct) is { } conflict)
			return conflict;
		entry.Enabled = enable ? null : false;
		List<string> failures = UserResolver.ValidateEntry(current, roles, registry, login, entry);
		if (failures.Count > 0)
			return EndpointHelpers.BadRequest(string.Join(Environment.NewLine, failures));
		await store.UpsertAsync(login, entry, ct);
		await resolver.EnsureFreshAsync(true, ct);
		return Results.Ok(new
		{
			user = ToDto(login, new MergedUser(entry, true, UserEditing.FindConfigUser(current, login) is not null)),
			warning = SelfEditWarning(principal, login, entry.Admin == true, enable)
		});
	}

	/// <summary>null = keep the stored value, "" = clear, anything else = run the secret policy.</summary>
	private static string? MergeSecret(
		string? requested, string? stored,
		Func<string, UserSecretPolicy.SecretResult> prepare, out string? error)
	{
		error = null;
		if (requested is null)
			return stored;
		if (requested.Length == 0)
			return null;
		UserSecretPolicy.SecretResult result = prepare(requested);
		error = result.Error;
		return result.Value;
	}

	private static UserDto ToDto(string login, MergedUser account)
	{
		UserOptions o = account.Options;
		Dictionary<string, RoleDto>? backends = o.Backends is { Count: > 0 }
			? o.Backends.ToDictionary(
				b => b.Key,
				b => new RoleDto(
					b.Value.Enabled, b.Value.Provider, b.Value.UserName,
					!string.IsNullOrEmpty(b.Value.Password),
					EndpointHelpers.MaskSecretSettings(b.Value.Settings)),
				StringComparer.OrdinalIgnoreCase)
			: null;
		return new UserDto(
			login,
			account.Invalid ? "db (invalid — refused)"
				: account.FromDatabase
				? o.AutoProvisioned == true ? "db (auto)"
				: account.ShadowsConfig ? "db (shadows config)" : "db"
				: "config",
			o.MailAddress,
			o.Admin == true,
			o.Enabled != false,
			!string.IsNullOrEmpty(o.Password),
			string.IsNullOrEmpty(o.Password) ? null :
			GatewayPasswordHasher.IsHashed(o.Password) ? "pbkdf2" : "PLAINTEXT",
			backends,
			ToSourceDto(account.Sources));
	}

	/// <summary>
	///   Per-field provenance — which level (`config`/`db`) supplied each value — so the admin
	///   editor can show an operator which of the values in front of them came from the config file
	///   (a value would otherwise silently turn into an invisible database override once resubmitted
	///   unchanged).
	///   Keyed by the same field paths <see cref="UserMerge" /> already computes them under
	///   ("MailAddress", "Backends:MailStore:UserName", …).
	/// </summary>
	private static Dictionary<string, string>? ToSourceDto(IReadOnlyDictionary<string, UserFieldSource>? sources)
	{
		return sources is { Count: > 0 }
			? sources.ToDictionary(
				p => p.Key,
				p => p.Value == UserFieldSource.UserDatabase ? "db" : "config",
				StringComparer.OrdinalIgnoreCase)
			: null;
	}
}
