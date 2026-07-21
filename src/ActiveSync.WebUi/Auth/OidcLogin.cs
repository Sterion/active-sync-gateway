using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Auth;

/// <summary>
///   The OIDC ticket → gateway-account mapping, separated from the handler wiring so the
///   decision matrix is unit-testable. Rules: the configured login claim must map to a
///   DECLARED account — unless AutoProvision creates one (a plain database row, visible in
///   `eas users`, admin-editable like any other). Admin = the account flag OR the
///   configured admin claim; a just-provisioned account has no flag yet, so ONLY the claim
///   can grant it admin.
/// </summary>
internal static class OidcLogin
{
	/// <param name="ClaimAdmin">
	///   Admin came from the IdP admin claim rather than the account flag. The session records
	///   that, because live revalidation re-derives admin from the account and cannot re-read a
	///   claim the ticket deliberately never carried into the session.
	/// </param>
	internal sealed record Verdict(
		bool Allowed, string? Login, bool IsAdmin, bool Provisioned, string? Reason, bool ClaimAdmin = false);

	/// <summary>The immutable subject claim; with MapInboundClaims off it arrives under its raw name.</summary>
	internal const string SubjectClaim = "sub";

	/// <param name="provisionAsync">
	///   Persists a JIT account (validate + upsert + resolver refresh); returns validation
	///   failures — a claim value the account rules reject is refused, never provisioned.
	/// </param>
	/// <param name="bindSubjectAsync">
	///   Records the IdP subject on an as-yet-unbound DATABASE account (trust on first use).
	///   Null disables the recording, leaving such accounts unbound.
	/// </param>
	internal static async Task<Verdict> EvaluateAsync(
		ClaimsPrincipal ticket, WebUiOidcOptions oidc,
		IReadOnlyDictionary<string, MergedAccount> mergedUsers,
		Func<string, AccountOptions, Task<IReadOnlyList<string>>> provisionAsync,
		Func<string, string, Task>? bindSubjectAsync = null)
	{
		string? login = ticket.FindFirst(oidc.LoginClaim)?.Value?.Trim();
		if (string.IsNullOrEmpty(login))
			return new Verdict(false, null, false, false, $"the token carries no '{oidc.LoginClaim}' claim");

		bool claimAdmin = HasAdminClaim(ticket, oidc);
		string? subject = ticket.FindFirst(SubjectClaim)?.Value?.Trim();
		if (mergedUsers.TryGetValue(login, out MergedAccount? account))
		{
			if (account.Options.Enabled == false)
				return new Verdict(false, login, false, false, "the account is disabled");

			// The login claim is user-mutable at several common IdPs, so a bound account is
			// keyed on the immutable subject and a login match alone never suffices.
			if (!string.IsNullOrEmpty(account.Options.OidcSubject))
			{
				if (!string.Equals(account.Options.OidcSubject, subject, StringComparison.Ordinal))
					return new Verdict(false, login, false, false,
						"the token's subject does not match the subject bound to this account");
			}
			else if (!string.IsNullOrEmpty(subject) && account.FromDatabase && bindSubjectAsync is not null)
			{
				// Trust on first use — and only for database accounts: binding a config-declared
				// one would mint a database row that shadows its configuration entry.
				await bindSubjectAsync(login, subject);
			}

			return new Verdict(true, login, account.Options.Admin == true || claimAdmin, false, null,
				claimAdmin && account.Options.Admin != true);
		}

		if (!oidc.AutoProvision)
			return new Verdict(false, login, false, false,
				"the login is not a declared account (enable Oidc:AutoProvision or add the user)");

		AccountOptions entry = new()
		{
			MailAddress = ticket.FindFirst("email")?.Value is { Length: > 0 } email ? email : null,
			OidcSubject = string.IsNullOrEmpty(subject) ? null : subject
		};
		IReadOnlyList<string> failures = await provisionAsync(login, entry);
		return failures.Count > 0
			? new Verdict(false, login, false, false, string.Join("; ", failures))
			: new Verdict(true, login, claimAdmin, true, null, claimAdmin);
	}

	/// <summary>
	///   The configured admin claim grants admin, and its value must match
	///   <see cref="WebUiOidcOptions.AdminClaimValue" /> exactly — or any value when that is the
	///   literal "*". Omitting the value grants NOTHING: "AdminClaim: groups" with no value
	///   would hand gateway admin to every user who has a groups claim, i.e. the whole
	///   directory, and that must not be reachable by omission. Startup validation refuses the
	///   combination as well; this stays fail-closed regardless.
	/// </summary>
	internal static bool HasAdminClaim(ClaimsPrincipal ticket, WebUiOidcOptions oidc)
	{
		if (string.IsNullOrWhiteSpace(oidc.AdminClaim) || string.IsNullOrWhiteSpace(oidc.AdminClaimValue))
			return false;
		return oidc.AdminClaimValue == "*"
			? ticket.Claims.Any(c => c.Type == oidc.AdminClaim)
			: ticket.HasClaim(oidc.AdminClaim, oidc.AdminClaimValue);
	}
}
