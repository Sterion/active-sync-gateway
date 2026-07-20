using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
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
						settings = b.Value.Settings is { Count: > 0 } ? b.Value.Settings : null
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

				IReadOnlyList<BackendConfigField> fields = [];
				if (provider is not null)
					try
					{
						fields = registry.GetFor(provider, role).DescribeConfiguration(role);
					}
					catch (InvalidOperationException)
					{
						// A provider that no longer serves the role: no form, raw editing only.
					}

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
			IOptionsMonitor<ActiveSyncOptions> options, ILoggerFactory loggerFactory, CancellationToken ct) =>
		{
			string? login = principal.Identity?.Name;
			if (login is null)
				return Results.Unauthorized();
			if (string.IsNullOrEmpty(request.Current) || string.IsNullOrEmpty(request.New))
				return Results.BadRequest(new { error = "current and new passwords are required" });

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
				return Results.BadRequest(new { error = "the current password is wrong" });
			}

			throttle.RecordSuccess(throttleKey);
			ActiveSyncOptions current = options.CurrentValue;
			AccountOptions entry = await AccountEditing.LoadStartingEntryAsync(store, current, login, ct);
			// Stored as a pbkdf2$ hash: this decouples the phone/web password from the mail
			// backend, exactly like the CLI's `eas user password`.
			entry.Password = GatewayPasswordHasher.Hash(request.New);
			List<string> failures = AccountResolver.ValidateEntry(current, roles, registry, login, entry);
			if (failures.Count > 0)
				return Results.BadRequest(new { error = string.Join(Environment.NewLine, failures) });
			await store.UpsertAsync(login, entry, ct);
			await resolver.EnsureFreshAsync(true, ct);
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
			if (!Enum.TryParse(roleName, true, out BackendRole role))
				return Results.BadRequest(new
				{
					error = $"'{roleName}' is not a backend role (roles: {string.Join(", ", Enum.GetNames<BackendRole>())})"
				});

			ActiveSyncOptions current = options.CurrentValue;
			AccountOptions entry = await AccountEditing.LoadStartingEntryAsync(store, current, login, ct);
			entry.Backends ??= new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase);
			if (!entry.Backends.TryGetValue(role.ToString(), out BackendRoleOverride? @override))
				entry.Backends[role.ToString()] = @override = new BackendRoleOverride();

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
						return Results.BadRequest(new { error = prepared.Error });
					@override.Password = prepared.Value;
				}
			}

			@override.Settings = request.Settings is { Count: > 0 }
				? new Dictionary<string, string?>(request.Settings, StringComparer.OrdinalIgnoreCase)
				: null;

			// An override that says nothing is dropped, not stored as noise.
			if (@override is { Enabled: null, Provider: null, UserName: null, Password: null, Settings: null })
			{
				entry.Backends.Remove(role.ToString());
				if (entry.Backends.Count == 0)
					entry.Backends = null;
			}

			List<string> failures = AccountResolver.ValidateEntry(current, roles, registry, login, entry);
			if (failures.Count > 0)
				return Results.BadRequest(new { error = string.Join(Environment.NewLine, failures) });
			await store.UpsertAsync(login, entry, ct);
			await resolver.EnsureFreshAsync(true, ct);
			return Results.Ok(new { login, role = role.ToString() });
		});
	}
}
