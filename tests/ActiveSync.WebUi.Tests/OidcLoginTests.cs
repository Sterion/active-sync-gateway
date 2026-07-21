using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.WebUi.Auth;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   The OIDC ticket → account decision matrix: declared/unknown logins × the account Admin
///   flag × the admin claim (with and without a required value) × AutoProvision, plus the
///   JIT-provision path (email claim, validation rejections, exactly-once persistence).
/// </summary>
public sealed class OidcLoginTests
{
	private static ClaimsPrincipal Ticket(params (string Type, string Value)[] claims)
	{
		return new ClaimsPrincipal(new ClaimsIdentity(
			claims.Select(c => new Claim(c.Type, c.Value)), "test"));
	}

	private static WebUiOidcOptions Oidc(
		bool autoProvision = false, string? adminClaim = null, string? adminClaimValue = null)
	{
		return new WebUiOidcOptions
		{
			Authority = "https://id.example.com",
			ClientId = "eas",
			AutoProvision = autoProvision,
			AdminClaim = adminClaim,
			AdminClaimValue = adminClaimValue
		};
	}

	private static Dictionary<string, MergedAccount> Users(params (string Login, bool Admin)[] users)
	{
		return users.ToDictionary(
			u => u.Login,
			u => new MergedAccount(new AccountOptions { Admin = u.Admin ? true : null }, true, false),
			StringComparer.OrdinalIgnoreCase);
	}

	private static Func<string, AccountOptions, Task<IReadOnlyList<string>>> NoProvision()
	{
		return (_, _) => throw new InvalidOperationException("provisioning must not be attempted");
	}

	[Fact]
	public async Task MissingLoginClaim_IsRejected()
	{
		OidcLogin.Verdict verdict = await OidcLogin.EvaluateAsync(
			Ticket(("email", "a@x")), Oidc(), Users(), NoProvision());
		Assert.False(verdict.Allowed);
		Assert.Contains("preferred_username", verdict.Reason);
	}

	[Fact]
	public async Task DisabledAccount_IsRejected_EvenWithValidClaims()
	{
		Dictionary<string, MergedAccount> users = new(StringComparer.OrdinalIgnoreCase)
		{
			["alice"] = new MergedAccount(new AccountOptions { Admin = true, Enabled = false }, true, false),
		};

		OidcLogin.Verdict verdict = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "alice")), Oidc(), users, NoProvision());
		Assert.False(verdict.Allowed);
		Assert.Contains("disabled", verdict.Reason);
	}

	[Fact]
	public async Task DeclaredAccount_SignsIn_AdminFromFlagOrClaim()
	{
		Dictionary<string, MergedAccount> users = Users(("alice", true), ("bob", false));

		// The account flag grants admin without any claim.
		OidcLogin.Verdict alice = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "alice")), Oidc(), users, NoProvision());
		Assert.True(alice is { Allowed: true, IsAdmin: true, Provisioned: false });

		// No flag, no claim: a plain sign-in.
		OidcLogin.Verdict bob = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "bob")), Oidc(adminClaim: "groups", adminClaimValue: "eas-admin"),
			users, NoProvision());
		Assert.True(bob is { Allowed: true, IsAdmin: false });

		// The matching claim value grants admin without the flag ("either grants admin").
		OidcLogin.Verdict bobAdmin = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "bob"), ("groups", "eas-admin")),
			Oidc(adminClaim: "groups", adminClaimValue: "eas-admin"), users, NoProvision());
		Assert.True(bobAdmin is { Allowed: true, IsAdmin: true });

		// A claim with the WRONG value grants nothing.
		OidcLogin.Verdict wrongValue = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "bob"), ("groups", "users")),
			Oidc(adminClaim: "groups", adminClaimValue: "eas-admin"), users, NoProvision());
		Assert.False(wrongValue.IsAdmin);

		// An explicit "*" is how "any value grants admin" is asked for.
		OidcLogin.Verdict wildcard = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "bob"), ("is-admin", "whatever")),
			Oidc(adminClaim: "is-admin", adminClaimValue: "*"), users, NoProvision());
		Assert.True(wildcard.IsAdmin);
	}

	[Fact]
	public void AdminClaim_WithoutARequiredValue_GrantsNothing()
	{
		// Reachable only by omission, and the documented example was AdminClaim: "groups" —
		// every user has groups. Startup validation refuses the combination now, but the claim
		// evaluation must fail closed too rather than rely on it.
		Assert.False(OidcLogin.HasAdminClaim(
			Ticket(("groups", "anything")), Oidc(adminClaim: "groups")));
		Assert.True(OidcLogin.HasAdminClaim(
			Ticket(("groups", "anything")), Oidc(adminClaim: "groups", adminClaimValue: "*")));
		Assert.False(OidcLogin.HasAdminClaim(
			Ticket(("other", "anything")), Oidc(adminClaim: "groups", adminClaimValue: "*")));
	}

	[Fact]
	public async Task UnknownLogin_Rejected_WithoutAutoProvision()
	{
		OidcLogin.Verdict verdict = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "stranger")), Oidc(), Users(("alice", false)), NoProvision());
		Assert.False(verdict.Allowed);
		Assert.Contains("AutoProvision", verdict.Reason);
	}

	[Fact]
	public async Task AutoProvision_CreatesTheAccountOnce_AdminOnlyFromClaim()
	{
		List<(string Login, AccountOptions Entry)> provisioned = [];
		Func<string, AccountOptions, Task<IReadOnlyList<string>>> provision = (login, entry) =>
		{
			provisioned.Add((login, entry));
			return Task.FromResult<IReadOnlyList<string>>([]);
		};

		// The JIT account carries the email claim; admin comes ONLY from the claim (no DB flag yet).
		OidcLogin.Verdict verdict = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "newbie"), ("email", "newbie@example.com"), ("groups", "eas-admin")),
			Oidc(autoProvision: true, adminClaim: "groups", adminClaimValue: "eas-admin"),
			Users(), provision);
		Assert.True(verdict is { Allowed: true, Provisioned: true, IsAdmin: true });
		(string login, AccountOptions entry) = Assert.Single(provisioned);
		Assert.Equal("newbie", login);
		Assert.Equal("newbie@example.com", entry.MailAddress);

		// Without the claim, a JIT account is never admin.
		OidcLogin.Verdict plain = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "plainuser")),
			Oidc(autoProvision: true, adminClaim: "groups", adminClaimValue: "eas-admin"),
			Users(), provision);
		Assert.True(plain is { Allowed: true, Provisioned: true, IsAdmin: false });

		// A login the account rules reject (':' would corrupt Basic auth) is refused, not stored.
		Func<string, AccountOptions, Task<IReadOnlyList<string>>> reject = (_, _) =>
			Task.FromResult<IReadOnlyList<string>>(["login must not contain ':'"]);
		OidcLogin.Verdict invalid = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "bad:login")), Oidc(autoProvision: true), Users(), reject);
		Assert.False(invalid.Allowed);
		Assert.Contains(":", invalid.Reason);
	}

	[Fact]
	public async Task DeclaredAccount_NeverProvisions_EvenWithAutoProvisionOn()
	{
		OidcLogin.Verdict verdict = await OidcLogin.EvaluateAsync(
			Ticket(("preferred_username", "alice")), Oidc(autoProvision: true),
			Users(("alice", false)), NoProvision());
		Assert.True(verdict is { Allowed: true, Provisioned: false });
	}
}
