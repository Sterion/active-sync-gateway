using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
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
		AccountFieldPaths.FieldPath? field = AccountFieldPaths.Find("Admin");
		Assert.NotNull(field);
		Assert.False(field.IsSecret);
		Assert.True(AccountFieldPaths.TryParseValue(field, "true", out object? value, out _));

		AccountOptions account = new();
		field.Set(account, value);
		Assert.True(account.Admin);

		field.Set(account, null);
		Assert.Null(account.Admin);

		Assert.False(AccountFieldPaths.TryParseValue(field, "maybe", out _, out string? error));
		Assert.Contains("not a valid", error);
	}

	[Fact]
	public void AdminFlag_JsonRoundTrip_AndLegacyRowsDeserializeAsUnset()
	{
		AccountOptions account = new() { Admin = true, MailAddress = "a@x" };
		string json = JsonSerializer.Serialize(account, AccountStore.JsonOptions);
		AccountOptions restored = JsonSerializer.Deserialize<AccountOptions>(json, AccountStore.JsonOptions)!;
		Assert.True(restored.Admin);

		// A row written before the flag existed (no "admin" property) deserializes as unset.
		AccountOptions legacy = JsonSerializer.Deserialize<AccountOptions>(
			"""{"mailAddress":"b@x"}""", AccountStore.JsonOptions)!;
		Assert.Null(legacy.Admin);

		// Unset stays absent on the wire (WhenWritingNull) — old binaries never see the key.
		Assert.DoesNotContain("admin", JsonSerializer.Serialize(new AccountOptions(), AccountStore.JsonOptions));
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

	[Fact]
	public void SecretPolicy_GatewayPassword_HashesPlaintext_PassesHash_RejectsSealed()
	{
		AccountSecretPolicy.SecretResult hashed = AccountSecretPolicy.PrepareGatewayPassword("hunter2");
		Assert.Null(hashed.Error);
		Assert.True(GatewayPasswordHasher.IsHashed(hashed.Value!));
		Assert.Equal(AccountSecretPolicy.PlaintextDisposition.Hashed, hashed.Plaintext);

		string preHashed = GatewayPasswordHasher.Hash("s3cret");
		AccountSecretPolicy.SecretResult passThrough = AccountSecretPolicy.PrepareGatewayPassword(preHashed);
		Assert.Equal(preHashed, passThrough.Value);
		Assert.Equal(AccountSecretPolicy.PlaintextDisposition.None, passThrough.Plaintext);

		Assert.NotNull(AccountSecretPolicy.PrepareGatewayPassword("pbkdf2$broken").Error);
		Assert.NotNull(AccountSecretPolicy.PrepareGatewayPassword("enc:v1:AAAA").Error);
	}

	[Fact]
	public void SecretPolicy_BackendPassword_SealsWithKey_PlainWithout_RejectsHash()
	{
		EncryptionOptions withKey = new() { Key = KeyBase64 };
		AccountSecretPolicy.SecretResult sealedResult =
			AccountSecretPolicy.PrepareBackendPassword("imap-pw", withKey, "Backends:MailStore:Password");
		Assert.Null(sealedResult.Error);
		Assert.True(SecretValue.IsSealed(sealedResult.Value!));
		Assert.Equal(AccountSecretPolicy.PlaintextDisposition.Sealed, sealedResult.Plaintext);

		// An already-sealed value passes through untouched.
		AccountSecretPolicy.SecretResult resealed =
			AccountSecretPolicy.PrepareBackendPassword(sealedResult.Value!, withKey, "Backends:MailStore:Password");
		Assert.Equal(sealedResult.Value, resealed.Value);

		EncryptionOptions withoutKey = new() { AllowPlaintext = true };
		AccountSecretPolicy.SecretResult plain =
			AccountSecretPolicy.PrepareBackendPassword("imap-pw", withoutKey, "Backends:MailStore:Password");
		Assert.Equal("imap-pw", plain.Value);
		Assert.Equal(AccountSecretPolicy.PlaintextDisposition.StoredPlaintext, plain.Plaintext);

		AccountSecretPolicy.SecretResult hash = AccountSecretPolicy.PrepareBackendPassword(
			GatewayPasswordHasher.Hash("x"), withKey, "Backends:MailStore:Password");
		Assert.NotNull(hash.Error);
		Assert.Contains("backend password", hash.Error);
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
