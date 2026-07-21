using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.WebUi.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   The user portal's self-service API. Every endpoint operates ONLY on the caller's own
///   account (the identity claim — never a route parameter), and the edit surface is
///   deliberately narrow: the gateway password and each role's backend credentials/settings.
///   Provider switches and role disablement change serving topology — that is administration
///   and stays admin-only. Writes run the same validate+seal+upsert pipeline as the CLI.
/// </summary>
internal static class PortalEndpoints
{
	internal sealed record PasswordChangeRequest(string? Current, string? New);

	/// <summary>Password sentinel: null = keep the stored value, "" = clear, value = seal.</summary>
	internal sealed record RoleSelfUpdate(
		string? UserName, string? Password, Dictionary<string, string?>? Settings);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("me", async (ClaimsPrincipal principal, AccountResolver resolver, CancellationToken ct) =>
		{
			string? login = principal.Identity?.Name;
			await resolver.EnsureFreshAsync(false, ct);
			if (login is null || !resolver.MergedUsers.TryGetValue(login, out MergedAccount? account))
				return Results.NotFound();
			AccountOptions o = account.Options;
			return Results.Ok(new
			{
				login,
				mailAddress = o.MailAddress,
				admin = o.Admin == true,
				passwordSet = !string.IsNullOrEmpty(o.Password),
				backends = o.Backends?.ToDictionary(
					b => b.Key,
					b => new
					{
						enabled = b.Value.Enabled,
						provider = b.Value.Provider,
						userName = b.Value.UserName,
						passwordSet = !string.IsNullOrEmpty(b.Value.Password),
						settings = EndpointHelpers.MaskSecretSettings(b.Value.Settings)
					},
					StringComparer.OrdinalIgnoreCase)
			});
		});

		// What the portal needs to render a form instead of asking a user to know that CardDAV
		// calls its server "BaseUrl": for each role, who serves it and which fields that
		// provider reads. Schemas are descriptions, never values — nothing configured leaks.
		api.MapGet("backends/meta", (
			ClaimsPrincipal principal, AccountResolver resolver,
			BackendRolesProvider rolesProvider, BackendProviderRegistry registry) =>
		{
			string? login = principal.Identity?.Name;
			if (login is null)
				return Results.Unauthorized();

			BackendRolesConfig roles = rolesProvider.Current;
			resolver.MergedUsers.TryGetValue(login, out MergedAccount? account);

			Dictionary<string, object> meta = new(StringComparer.OrdinalIgnoreCase);
			foreach (BackendRole role in Enum.GetValues<BackendRole>())
			{
				// The user's own provider override wins, exactly as AccountResolver resolves it.
				string? provider = null;
				if (account?.Options.Backends?.TryGetValue(role.ToString(), out BackendRoleOverride? own) == true)
					provider = own.Provider;
				if (string.IsNullOrWhiteSpace(provider))
					provider = roles.Assignments.TryGetValue(role, out RoleAssignment? assignment)
						? assignment.ProviderName
						: null;

				// ONLY the self-service fields: this drives the portal's form, and a field it
				// renders but the save below refuses is a form that cannot be submitted.
				IReadOnlyList<BackendConfigField> fields = SelfServiceFields(registry, provider, role);

				meta[role.ToString()] = new
				{
					provider,
					fields = fields.Select(f => new
					{
						name = f.Name, label = f.Label, type = f.Type.ToString(), required = f.Required,
						@default = f.Default, enumValues = f.EnumValues, help = f.Help, min = f.Min, max = f.Max
					})
				};
			}

			return Results.Ok(meta);
		});

		api.MapPut("password", async (
			PasswordChangeRequest request, ClaimsPrincipal principal, HttpContext http,
			AccountStore store, AccountResolver resolver, IBackendSessionFactory sessionFactory,
			AuthThrottle throttle, BackendRolesConfig roles, BackendProviderRegistry registry,
			SyncStateService state, IOptionsMonitor<ActiveSyncOptions> options,
			ILoggerFactory loggerFactory, CancellationToken ct) =>
		{
			string? login = principal.Identity?.Name;
			if (login is null)
				return Results.Unauthorized();
			if (string.IsNullOrEmpty(request.Current) || string.IsNullOrEmpty(request.New))
				return EndpointHelpers.BadRequest("current and new passwords are required");

			// Re-verify the CURRENT password (same verdict path as login), throttled — a
			// hijacked session must not be enough to take the account over.
			string address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
			string throttleKey = $"webui:{address}\n{login}";
			if (throttle.BlockedForSeconds(throttleKey) is { } retryAfter)
			{
				http.Response.Headers.RetryAfter = retryAfter.ToString();
				return Results.StatusCode(StatusCodes.Status429TooManyRequests);
			}

			bool verified;
			try
			{
				verified = await sessionFactory.AuthenticateAsync(new BackendCredentials(login, request.Current), ct);
			}
			catch (BackendException)
			{
				return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
			}
			catch (KeyNotFoundException)
			{
				verified = false; // unconfigured gateway and no local rule — nothing to verify against
			}

			if (!verified)
			{
				throttle.RecordFailure(throttleKey);
				return EndpointHelpers.BadRequest("the current password is wrong");
			}

			throttle.RecordSuccess(throttleKey);
			ActiveSyncOptions current = options.CurrentValue;
			AccountOptions entry = await AccountEditing.LoadStartingEntryAsync(store, current, login, ct);
			// Stored as a pbkdf2$ hash: this decouples the phone/web password from the mail
			// backend, exactly like the CLI's `eas user password`.
			entry.Password = GatewayPasswordHasher.Hash(request.New);
			List<string> failures = AccountResolver.ValidateEntry(current, roles, registry, login, entry);
			if (failures.Count > 0)
				return EndpointHelpers.BadRequest(string.Join(Environment.NewLine, failures));
			await store.UpsertAsync(login, entry, ct);
			await resolver.EnsureFreshAsync(true, ct);
			// A password change signs every OTHER session of this login out — including any
			// session an attacker still holds a cookie for, which is the point of changing it.
			// This browser is then re-signed-in so the user is not thrown out of their own act.
			await state.RevokeSessionsBeforeAsync(login, DateTime.UtcNow, ct);
			await http.SignInAsync(WebUiAuth.Scheme, ReissueSession(principal));
			loggerFactory.CreateLogger("ActiveSync.WebUi.Portal")
				.LogInformation("Portal password change for {User}", login);
			return Results.Ok(new { login, passwordSet = true });
		});

		api.MapPut("backends/{roleName}", async (
			string roleName, RoleSelfUpdate request, ClaimsPrincipal principal,
			AccountStore store, AccountResolver resolver,
			BackendRolesConfig roles, BackendProviderRegistry registry,
			IOptionsMonitor<ActiveSyncOptions> options, CancellationToken ct) =>
		{
			string? login = principal.Identity?.Name;
			if (login is null)
				return Results.Unauthorized();
			if (!EndpointHelpers.TryParseRole(roleName, out BackendRole role, out IResult? roleError))
				return roleError!;

			ActiveSyncOptions current = options.CurrentValue;
			AccountOptions entry = await AccountEditing.LoadStartingEntryAsync(store, current, login, ct);
			entry.Backends ??= new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase);
			if (!entry.Backends.TryGetValue(role.ToString(), out BackendRoleOverride? @override))
				entry.Backends[role.ToString()] = @override = new BackendRoleOverride();

			// The settings dictionary is NOT a self-service surface by default. A user key
			// replaces the whole global subtree it addresses, so accepting it wholesale let a
			// non-admin point their own role at any host and collect the credential the gateway
			// presents there. Only fields the provider marks SelfServiceEditable are writable;
			// everything else keeps whatever an administrator put on the account.
			string? effectiveProvider = @override.Provider
				?? (roles.Assignments.TryGetValue(role, out RoleAssignment? assignment)
					? assignment.ProviderName
					: null);
			HashSet<string> editable = new(
				SelfServiceFields(registry, effectiveProvider, role).Select(f => f.Name),
				StringComparer.OrdinalIgnoreCase);
			List<string> refused =
			[
				.. (request.Settings ?? [])
					.Select(pair => pair.Key)
					.Where(key => !editable.Contains(BackendConfigValidation.ListRoot(key)))
					.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
			];
			if (refused.Count > 0)
				return EndpointHelpers.BadRequest(
					"these settings are administered for you and cannot be changed here: " +
					string.Join(", ", refused),
					refused.Select(key => new BackendsEndpoints.FailureDto(
						key, "This setting can only be changed by an administrator.")));

			// Deliberately untouched: Enabled and Provider (admin-only surface).
			@override.UserName = string.IsNullOrWhiteSpace(request.UserName) ? null : request.UserName.Trim();
			if (request.Password is not null)
			{
				if (request.Password.Length == 0)
				{
					@override.Password = null;
				}
				else
				{
					AccountSecretPolicy.SecretResult prepared = AccountSecretPolicy.PrepareBackendPassword(
						request.Password, current.Encryption, $"Backends:{role}:Password");
					if (prepared.Error is not null)
						return EndpointHelpers.BadRequest(prepared.Error);
					@override.Password = prepared.Value;
				}
			}

			// Keep every stored key the caller may not touch, replace the editable ones wholesale
			// (an omitted editable key still means "cleared", as it always did). A re-posted mask
			// sentinel resolves back to the stored secret so masking on read (C5) can't clobber it.
			Dictionary<string, string?>? storedSettings = @override.Settings;
			Dictionary<string, string?> merged = new(StringComparer.OrdinalIgnoreCase);
			foreach ((string key, string? value) in storedSettings ?? [])
				if (!editable.Contains(BackendConfigValidation.ListRoot(key)))
					merged[key] = value;
			foreach ((string key, string? value) in EndpointHelpers.UnmaskSecretSettings(request.Settings, storedSettings) ?? [])
				merged[key] = value;
			@override.Settings = merged.Count > 0 ? merged : null;

			// An override that says nothing is dropped, not stored as noise.
			if (@override is { Enabled: null, Provider: null, UserName: null, Password: null, Settings: null })
			{
				entry.Backends.Remove(role.ToString());
				if (entry.Backends.Count == 0)
					entry.Backends = null;
			}

			List<string> failures = AccountResolver.ValidateEntry(current, roles, registry, login, entry);
			if (failures.Count > 0)
				return EndpointHelpers.BadRequest(string.Join(Environment.NewLine, failures));
			await store.UpsertAsync(login, entry, ct);
			await resolver.EnsureFreshAsync(true, ct);
			return Results.Ok(new { login, role = role.ToString() });
		});
	}

	/// <summary>
	///   The fields of <paramref name="providerName" />'s schema for the role that an account
	///   holder may set for themselves. Empty for an unknown provider, a provider that no longer
	///   serves the role, or one that describes nothing — a plugin is administration-only until
	///   it opts a field in, which is the point of the flag defaulting to false.
	/// </summary>
	private static IReadOnlyList<BackendConfigField> SelfServiceFields(
		BackendProviderRegistry registry, string? providerName, BackendRole role)
	{
		if (string.IsNullOrWhiteSpace(providerName))
			return [];
		try
		{
			return [.. registry.GetFor(providerName, role).DescribeConfiguration(role)
				.Where(field => field.SelfServiceEditable)];
		}
		catch (InvalidOperationException)
		{
			return [];
		}
	}

	/// <summary>
	///   A fresh session for the caller, carrying the same capability claims but a new start
	///   stamp — so a revocation that just invalidated every older session spares this one.
	/// </summary>
	private static ClaimsPrincipal ReissueSession(ClaimsPrincipal principal)
	{
		List<Claim> claims =
		[
			new Claim(ClaimTypes.Name, principal.Identity!.Name!),
			SessionValidation.SessionStart(DateTimeOffset.UtcNow)
		];
		if (principal.HasClaim(WebUiAuth.AdminClaim, "true"))
			claims.Add(new Claim(WebUiAuth.AdminClaim, "true"));
		if (principal.HasClaim(SessionValidation.AdminSourceClaim, SessionValidation.OidcAdminSource))
			claims.Add(new Claim(SessionValidation.AdminSourceClaim, SessionValidation.OidcAdminSource));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, WebUiAuth.Scheme));
	}
}
