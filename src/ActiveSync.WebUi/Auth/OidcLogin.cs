using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Auth;

/// <summary>
///   The OIDC ticket → gateway-account mapping, separated from the handler wiring so the
///   decision matrix is unit-testable. Rules: the configured login claim must map to a
///   DECLARED account — unless AutoProvision creates one (a plain database row, visible in
///   `eas user list`, admin-editable like any other). Admin = the account flag OR the
///   configured admin claim; a just-provisioned account has no flag yet, so ONLY the claim
///   can grant it admin.
/// </summary>
internal static class OidcLogin
{
	internal sealed record Verdict(bool Allowed, string? Login, bool IsAdmin, bool Provisioned, string? Reason);

	/// <param name="provisionAsync">
	///   Persists a JIT account (validate + upsert + resolver refresh); returns validation
	///   failures — a claim value the account rules reject is refused, never provisioned.
	/// </param>
	internal static async Task<Verdict> EvaluateAsync(
		ClaimsPrincipal ticket, WebUiOidcOptions oidc,
		IReadOnlyDictionary<string, MergedAccount> mergedUsers,
		Func<string, AccountOptions, Task<IReadOnlyList<string>>> provisionAsync)
	{
		string? login = ticket.FindFirst(oidc.LoginClaim)?.Value?.Trim();
		if (string.IsNullOrEmpty(login))
			return new Verdict(false, null, false, false, $"the token carries no '{oidc.LoginClaim}' claim");

		bool claimAdmin = HasAdminClaim(ticket, oidc);
		if (mergedUsers.TryGetValue(login, out MergedAccount? account))
			return new Verdict(true, login, account.Options.Admin == true || claimAdmin, false, null);

		if (!oidc.AutoProvision)
			return new Verdict(false, login, false, false,
				"the login is not a declared account (enable Oidc:AutoProvision or add the user)");

		AccountOptions entry = new()
		{
			MailAddress = ticket.FindFirst("email")?.Value is { Length: > 0 } email ? email : null
		};
		IReadOnlyList<string> failures = await provisionAsync(login, entry);
		return failures.Count > 0
			? new Verdict(false, login, false, false, string.Join("; ", failures))
			: new Verdict(true, login, claimAdmin, true, null);
	}

	/// <summary>The configured admin claim grants admin; with a value set, it must match exactly.</summary>
	internal static bool HasAdminClaim(ClaimsPrincipal ticket, WebUiOidcOptions oidc)
	{
		if (string.IsNullOrWhiteSpace(oidc.AdminClaim))
			return false;
		return string.IsNullOrWhiteSpace(oidc.AdminClaimValue)
			? ticket.Claims.Any(c => c.Type == oidc.AdminClaim)
			: ticket.HasClaim(oidc.AdminClaim, oidc.AdminClaimValue);
	}
}
