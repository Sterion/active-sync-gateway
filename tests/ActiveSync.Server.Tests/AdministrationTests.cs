using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Crypto;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The shared administration plumbing lifted into Core for the CLI and web UI: the Admin
///   account flag (field path + JSON round-trip), the WebUi settings-catalogue entries, the
///   secret-preparation policy, and the OIDC options validation.
/// </summary>
public sealed class AdministrationTests
{
	private const string KeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

	[Fact]
	public void AdminFieldPath_SetsAndClearsTheFlag()
	{
		UserFieldPaths.FieldPath? field = UserFieldPaths.Find("Admin");
		Assert.NotNull(field);
		Assert.False(field.IsSecret);
		Assert.True(UserFieldPaths.TryParseValue(field, "true", out object? value, out _));

		UserOptions account = new();
		field.Set(account, value);
		Assert.True(account.Admin);

		field.Set(account, null);
		Assert.Null(account.Admin);

		Assert.False(UserFieldPaths.TryParseValue(field, "maybe", out _, out string? error));
		Assert.Contains("not a valid", error);
	}

	// The gateway-vs-backend password distinction is an explicit flag now, not inferred from
	// whether the field key contains a ':'. This guards that the flag matches the old heuristic's
	// (correct) answers, so callers can rely on it instead of string-sniffing.
	[Fact]
	public void FieldPath_MarksOnlyTheGatewayPasswordAsGatewayPassword()
	{
		UserFieldPaths.FieldPath gateway = UserFieldPaths.Find("Password")!;
		Assert.True(gateway.IsSecret);
		Assert.True(gateway.IsGatewayPassword);

		foreach (string backendKey in UserFieldPaths.BackendSecretKeys)
		{
			UserFieldPaths.FieldPath backend = UserFieldPaths.Find(backendKey)!;
			Assert.True(backend.IsSecret);
			Assert.False(backend.IsGatewayPassword);
		}

		// A non-secret field is neither.
		UserFieldPaths.FieldPath mail = UserFieldPaths.Find("MailAddress")!;
		Assert.False(mail.IsSecret);
		Assert.False(mail.IsGatewayPassword);
	}

	[Fact]
	public void AdminFlag_JsonRoundTrip_AndLegacyRowsDeserializeAsUnset()
	{
		UserOptions account = new() { Admin = true, MailAddress = "a@x" };
		string json = JsonSerializer.Serialize(account, UserStore.JsonOptions);
		UserOptions restored = JsonSerializer.Deserialize<UserOptions>(json, UserStore.JsonOptions)!;
		Assert.True(restored.Admin);

		// A row written before the flag existed (no "admin" property) deserializes as unset.
		UserOptions legacy = JsonSerializer.Deserialize<UserOptions>(
			"""{"mailAddress":"b@x"}""", UserStore.JsonOptions)!;
		Assert.Null(legacy.Admin);

		// Unset stays absent on the wire (WhenWritingNull) — old binaries never see the key.
		Assert.DoesNotContain("admin", JsonSerializer.Serialize(new UserOptions(), UserStore.JsonOptions));
	}

	[Theory]
	[InlineData("ActiveSync:WebUi:Admin:Enabled", false, false)]
	[InlineData("ActiveSync:WebUi:UserPortal:Enabled", false, false)]
	[InlineData("ActiveSync:WebUi:Oidc:Authority", true, false)]
	[InlineData("ActiveSync:WebUi:Oidc:ClientSecret", true, true)]
	[InlineData("ActiveSync:WebUi:Oidc:AdminClaim", false, false)]
	[InlineData("ActiveSync:WebUi:Oidc:AutoProvision", false, false)]
	public void WebUiSettingKeys_AreInTheCatalogue(string key, bool restart, bool secret)
	{
		SettingKeys.SettingKey? definition = SettingKeys.Find(key);
		Assert.NotNull(definition);
		Assert.Equal(restart, definition.Restart);
		Assert.Equal(secret, definition.Secret);
	}

	[Fact]
	public void BackendPasswordSettingKeys_AreSecretFlagged()
	{
		// The synthetic open-ended backend entries mask password leaves.
		Assert.True(SettingKeys.Find("ActiveSync:Backends:MailStore:Password") is { Secret: true });
		Assert.True(SettingKeys.Find("ActiveSync:Backends:MailStore:Host") is { Secret: false });
	}

	// Every other AuthOptions member is catalogued; TrustedProxies (a List<string>, so neither
	// the bare key nor an indexed element resolves) is not, so `eas config set`/the admin Settings
	// page answer "not a recognized setting" for a security-relevant, documented-as-DB-settable
	// option (docs/configuration.md's Auth table, and AGENTS.md's "every setting is CLI-settable").
	[Theory]
	[InlineData("ActiveSync:Auth:TrustedProxies:0")]
	[InlineData("ActiveSync:Auth:TrustedProxies:1")]
	public void TrustedProxies_IndexedElement_IsSettableThroughTheCatalogue(string key)
	{
		SettingKeys.SettingKey? definition = SettingKeys.Find(key);
		Assert.NotNull(definition);
		Assert.False(definition.Restart);
		Assert.False(definition.Secret);
	}

	[Fact]
	public void SecretPolicy_GatewayPassword_HashesPlaintext_PassesHash_RejectsSealed()
	{
		// Behaviour change: plaintext gateway passwords now have a strength floor, so this
		// sample is >= UserSecretPolicy.MinGatewayPasswordLength (it was "hunter2", now rejected).
		UserSecretPolicy.SecretResult hashed = UserSecretPolicy.PrepareGatewayPassword("hunter2-strong");
		Assert.Null(hashed.Error);
		Assert.True(GatewayPasswordHasher.IsHashed(hashed.Value!));
		Assert.Equal(UserSecretPolicy.PlaintextDisposition.Hashed, hashed.Plaintext);

		string preHashed = GatewayPasswordHasher.Hash("s3cret");
		UserSecretPolicy.SecretResult passThrough = UserSecretPolicy.PrepareGatewayPassword(preHashed);
		Assert.Equal(preHashed, passThrough.Value);
		Assert.Equal(UserSecretPolicy.PlaintextDisposition.None, passThrough.Plaintext);

		Assert.NotNull(UserSecretPolicy.PrepareGatewayPassword("pbkdf2$broken").Error);
		Assert.NotNull(UserSecretPolicy.PrepareGatewayPassword("enc:v1:AAAA").Error);
	}

	[Fact]
	public void SecretPolicy_GatewayPassword_EnforcesStrengthFloor()
	{
		// The shared gateway-password policy now imposes a minimum length, so every write
		// surface — CLI 'user password', the admin API, and the self-service portal that used to
		// call GatewayPasswordHasher.Hash directly — rejects a trivially weak password identically.
		Assert.NotNull(UserSecretPolicy.PrepareGatewayPassword("short").Error); // below the floor
		Assert.Null(UserSecretPolicy.PrepareGatewayPassword("a-strong-passphrase").Error);
	}

	[Fact]
	public void SecretPolicy_GatewayPassword_RejectsEmpty_ClosingTheBypass()
	{
		// An empty gateway Password used to be hashed into a valid pbkdf2$ credential.
		// GatewayPasswordHasher.Verify(Hash(""), "") returns true, so the account authenticated
		// locally against a hash of the empty string and the backend was NEVER probed — a phone
		// sending an empty Basic-auth password got in. The policy must refuse it outright.
		foreach (string blank in new[] { "", "   ", "\t" })
		{
			UserSecretPolicy.SecretResult result = UserSecretPolicy.PrepareGatewayPassword(blank);
			Assert.NotNull(result.Error);
			Assert.Null(result.Value);
		}
	}

	[Fact]
	public void SecretPolicy_BackendPassword_SealsWithKey_PlainWithout_RejectsHash()
	{
		EncryptionOptions withKey = new() { Key = KeyBase64 };
		UserSecretPolicy.SecretResult sealedResult =
			UserSecretPolicy.PrepareBackendPassword("imap-pw", withKey, "Backends:MailStore:Password");
		Assert.Null(sealedResult.Error);
		Assert.True(SecretValue.IsSealed(sealedResult.Value!));
		Assert.Equal(UserSecretPolicy.PlaintextDisposition.Sealed, sealedResult.Plaintext);

		// An already-sealed value passes through untouched.
		UserSecretPolicy.SecretResult resealed =
			UserSecretPolicy.PrepareBackendPassword(sealedResult.Value!, withKey, "Backends:MailStore:Password");
		Assert.Equal(sealedResult.Value, resealed.Value);

		EncryptionOptions withoutKey = new() { AllowPlaintext = true };
		UserSecretPolicy.SecretResult plain =
			UserSecretPolicy.PrepareBackendPassword("imap-pw", withoutKey, "Backends:MailStore:Password");
		Assert.Equal("imap-pw", plain.Value);
		Assert.Equal(UserSecretPolicy.PlaintextDisposition.StoredPlaintext, plain.Plaintext);

		UserSecretPolicy.SecretResult hash = UserSecretPolicy.PrepareBackendPassword(
			GatewayPasswordHasher.Hash("x"), withKey, "Backends:MailStore:Password");
		Assert.NotNull(hash.Error);
		Assert.Contains("backend password", hash.Error);
	}

	[Fact]
	public void SecretPolicy_BackendPassword_MisconfiguredKey_RefusesPlaintext()
	{
		// A key that is CONFIGURED but fails to load (Key and KeyFile both set) used to be
		// swallowed — TryLoadKey's error was discarded and null was read as "no key", so the
		// backend password was silently written in plaintext under a broken encryption config.
		EncryptionOptions broken = new() { Key = "some-key", KeyFile = "/nonexistent/key" };
		UserSecretPolicy.SecretResult result =
			UserSecretPolicy.PrepareBackendPassword("imap-pw", broken, "Backends:MailStore:Password");
		Assert.NotNull(result.Error);
		Assert.Null(result.Value);
		Assert.NotEqual(UserSecretPolicy.PlaintextDisposition.StoredPlaintext, result.Plaintext);
	}

	[Fact]
	public void OidcValidation_RequiresAuthorityClientIdPair_AndClaimConsistency()
	{
		ActiveSyncOptionsValidator validator = new();
		ActiveSyncOptions Valid()
		{
			return new ActiveSyncOptions { Encryption = { AllowPlaintext = true } };
		}

		// No Oidc section at all: fine.
		Assert.True(validator.Validate(null, Valid()).Succeeded);

		// ClientId without Authority: OIDC intent without the issuer.
		ActiveSyncOptions noAuthority = Valid();
		noAuthority.WebUi.Oidc = new WebUiOidcOptions { ClientId = "eas" };
		ValidateOptionsResult result = validator.Validate(null, noAuthority);
		Assert.True(result.Failed);
		Assert.Contains(result.Failures!, f => f.Contains("Authority is required"));

		// Authority without ClientId.
		ActiveSyncOptions noClient = Valid();
		noClient.WebUi.Oidc = new WebUiOidcOptions { Authority = "https://id.example.com" };
		Assert.Contains(validator.Validate(null, noClient).Failures!, f => f.Contains("ClientId is required"));

		// A non-URL authority.
		ActiveSyncOptions badUrl = Valid();
		badUrl.WebUi.Oidc = new WebUiOidcOptions { Authority = "not a url", ClientId = "eas" };
		Assert.Contains(validator.Validate(null, badUrl).Failures!, f => f.Contains("absolute http(s) URL"));

		// AdminClaimValue without AdminClaim (independent of the authority pair).
		ActiveSyncOptions orphanValue = Valid();
		orphanValue.WebUi.Oidc = new WebUiOidcOptions { AdminClaimValue = "eas-admin" };
		Assert.Contains(validator.Validate(null, orphanValue).Failures!, f => f.Contains("requires AdminClaim"));

		// A complete section passes.
		ActiveSyncOptions ok = Valid();
		ok.WebUi.Oidc = new WebUiOidcOptions
		{
			Authority = "https://id.example.com/realms/main",
			ClientId = "eas",
			AdminClaim = "groups",
			AdminClaimValue = "eas-admin",
			AutoProvision = true
		};
		Assert.True(validator.Validate(null, ok).Succeeded);
	}
}
