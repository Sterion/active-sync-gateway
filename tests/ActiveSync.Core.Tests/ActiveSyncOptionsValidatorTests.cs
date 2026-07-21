using ActiveSync.Core.Options;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

public class ActiveSyncOptionsValidatorTests
{
	private static readonly ActiveSyncOptionsValidator Validator = new();

	private static readonly string TestKey = Convert.ToBase64String(new byte[32]);

	// Backend endpoints live in the role sections and are validated by the providers via
	// BackendConfigurationValidator — this validator only covers the host options.
	private static ActiveSyncOptions Valid()
	{
		return new ActiveSyncOptions { Encryption = new EncryptionOptions { Key = TestKey } };
	}

	[Fact]
	public void ValidMailOnlyConfig_Passes()
	{
		ValidateOptionsResult result = Validator.Validate(null, Valid());
		Assert.True(result.Succeeded);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(70000)]
	public void TlsPortOutOfRange_Fails_UnlessDisabled(int port)
	{
		ActiveSyncOptions options = Valid();
		options.Tls.Port = port;
		Assert.True(Validator.Validate(null, options).Failed);
		options.Tls.Enabled = false;
		Assert.True(Validator.Validate(null, options).Succeeded);
	}

	[Fact]
	public void Tls_CertificateKeyPathWithoutCertificatePath_Fails()
	{
		ActiveSyncOptions options = Valid();
		options.Tls.CertificateKeyPath = "/certs/privkey.pem";
		Assert.True(Validator.Validate(null, options).Failed);
	}

	[Fact]
	public void Tls_MissingCertificateFile_Fails()
	{
		ActiveSyncOptions options = Valid();
		options.Tls.CertificatePath = "/no/such/cert.pfx";
		Assert.True(Validator.Validate(null, options).Failed);
	}

	[Theory]
	[InlineData("Fancy", "Text")]
	[InlineData("Standard", "Xml")]
	public void UnknownLogModeOrFormat_Fails(string mode, string format)
	{
		ActiveSyncOptions options = Valid();
		options.Log.Mode = mode;
		options.Log.Format = format;
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("ActiveSync:Log", string.Join(";", result.Failures!));
	}

	[Theory]
	[InlineData("simple", "text")]
	[InlineData("Standard", "Json")]
	[InlineData("EXTENDED", "JSON")]
	public void ValidLogModeAndFormat_Succeed_CaseInsensitive(string mode, string format)
	{
		ActiveSyncOptions options = Valid();
		options.Log.Mode = mode;
		options.Log.Format = format;
		Assert.True(Validator.Validate(null, options).Succeeded);
	}

	[Fact]
	public void InvalidHeartbeatBounds_Fail()
	{
		ActiveSyncOptions options = Valid();
		options.Eas.MinHeartbeatSeconds = 900;
		options.Eas.MaxHeartbeatSeconds = 100;
		Assert.True(Validator.Validate(null, options).Failed);
	}

	[Fact]
	public void NoEncryptionKey_WithoutAllowPlaintext_Fails_WithActionableMessage()
	{
		ActiveSyncOptions options = Valid();
		options.Encryption = new EncryptionOptions();
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		string failures = string.Join(";", result.Failures!);
		Assert.Contains("Encryption", failures);
		Assert.Contains("openssl rand -base64 32", failures);
		Assert.Contains("AllowPlaintext", failures);
	}

	[Fact]
	public void AllowPlaintext_WithoutKey_Passes()
	{
		ActiveSyncOptions options = Valid();
		options.Encryption = new EncryptionOptions { AllowPlaintext = true };
		Assert.True(Validator.Validate(null, options).Succeeded);
	}

	[Fact]
	public void KeyAndKeyFileTogether_Fails()
	{
		ActiveSyncOptions options = Valid();
		options.Encryption = new EncryptionOptions { Key = TestKey, KeyFile = "somewhere.key" };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("not both", string.Join(";", result.Failures!));
	}

	[Theory]
	[InlineData("dG9vc2hvcnQ=")] // valid base64 but not 32 bytes → treated as a passphrase
	[InlineData("!!!not-base64!!!")] // not base64 at all → passphrase
	[InlineData("pass")] // short passphrases are the operator's call (warned, never rejected)
	public void AnyKeyString_IsAcceptedAsAPassphrase(string key)
	{
		ActiveSyncOptions options = Valid();
		options.Encryption = new EncryptionOptions { Key = key };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Succeeded, string.Join(";", result.Failures ?? []));
	}

	[Fact]
	public void PostgresUriConnectionString_Passes()
	{
		ActiveSyncOptions options = Valid();
		options.Database.ConnectionString =
			"postgresql://activesync:pw@active-sync-db-rw.active-sync:5432/activesync";
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Succeeded, string.Join(";", result.Failures ?? []));
	}

	[Fact]
	public void PostgresUriWithoutDatabase_FailsWithClearMessage()
	{
		ActiveSyncOptions options = Valid();
		options.Database.ConnectionString = "postgresql://activesync:pw@host:5432";
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("database name", string.Join(";", result.Failures!));
	}

	[Fact]
	public void MissingKeyFile_Fails()
	{
		ActiveSyncOptions options = Valid();
		options.Encryption = new EncryptionOptions { KeyFile = Path.Combine(Path.GetTempPath(), "no-such.key") };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("does not exist", string.Join(";", result.Failures!));
	}

	[Fact]
	public void RequireDeclaredUsers_WithoutConfigUsers_IsValid()
	{
		// Declared users may live in the state database (eas user add), which the validator
		// cannot see — an empty config Users list is no longer a hard failure.
		ActiveSyncOptions options = Valid();
		options.RequireDeclaredUsers = true;
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Succeeded);
	}

	[Fact]
	public void Policy_ValidFullConfig_Passes()
	{
		ActiveSyncOptions options = Valid();
		options.Policy = new PolicyOptions
		{
			Enabled = true,
			DevicePasswordEnabled = true,
			MinDevicePasswordLength = 6,
			MinDevicePasswordComplexCharacters = 2,
			MaxInactivityTimeDeviceLock = 300,
			MaxDevicePasswordFailedAttempts = 8,
			DevicePasswordExpiration = 0,
			DevicePasswordHistory = 3,
			MaxAttachmentSize = 10_485_760,
			PasswordRecoveryEnabled = true
		};
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Succeeded, string.Join(";", result.Failures ?? []));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(17)]
	public void Policy_MinPasswordLengthOutOfRange_Fails(int length)
	{
		ActiveSyncOptions options = Valid();
		options.Policy = new PolicyOptions { MinDevicePasswordLength = length };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("MinDevicePasswordLength", string.Join(";", result.Failures!));
	}

	[Theory]
	[InlineData(3)]
	[InlineData(17)]
	public void Policy_FailedAttemptsOutOfRange_Fails(int attempts)
	{
		ActiveSyncOptions options = Valid();
		options.Policy = new PolicyOptions { MaxDevicePasswordFailedAttempts = attempts };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("MaxDevicePasswordFailedAttempts", string.Join(";", result.Failures!));
	}

	[Fact]
	public void Policy_RangesValidatedEvenWhenDisabled()
	{
		// A typo must surface before the operator flips Enabled and ships it to the fleet.
		ActiveSyncOptions options = Valid();
		options.Policy = new PolicyOptions { Enabled = false, MinDevicePasswordComplexCharacters = 9 };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("MinDevicePasswordComplexCharacters", string.Join(";", result.Failures!));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(10000)]
	public void Policy_InactivityLockOutOfRange_Fails(int seconds)
	{
		ActiveSyncOptions options = Valid();
		options.Policy = new PolicyOptions { MaxInactivityTimeDeviceLock = seconds };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains("MaxInactivityTimeDeviceLock", string.Join(";", result.Failures!));
	}

	[Fact]
	public void Policy_NegativeSizesAndCounters_Fail()
	{
		ActiveSyncOptions options = Valid();
		options.Policy = new PolicyOptions
		{
			DevicePasswordExpiration = -1,
			DevicePasswordHistory = -2,
			MaxAttachmentSize = -3
		};
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		string failures = string.Join(";", result.Failures!);
		Assert.Contains("DevicePasswordExpiration", failures);
		Assert.Contains("DevicePasswordHistory", failures);
		Assert.Contains("MaxAttachmentSize", failures);
	}

	[Fact]
	public void ValidKeyFile_Passes()
	{
		string path = Path.Combine(Path.GetTempPath(), $"activesync-test-{Guid.NewGuid():N}.key");
		File.WriteAllText(path, TestKey + Environment.NewLine);
		try
		{
			ActiveSyncOptions options = Valid();
			options.Encryption = new EncryptionOptions { KeyFile = path };
			Assert.True(Validator.Validate(null, options).Succeeded);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Oidc_Enabled_ButIncomplete_Fails()
	{
		ActiveSyncOptions options = Valid();
		// Authority present signals OIDC intent; ClientId missing must fail while enabled.
		options.WebUi.Oidc = new WebUiOidcOptions { Authority = "https://id.example.com" };
		ValidateOptionsResult result = Validator.Validate(null, options);
		Assert.True(result.Failed);
		Assert.Contains(result.Failures!, f => f.Contains("Oidc:ClientId"));
	}

	[Fact]
	public void Oidc_Disabled_IsInert_EvenWhenIncomplete()
	{
		ActiveSyncOptions options = Valid();
		// The settings are kept but the block is switched off — validation must not object.
		options.WebUi.Oidc = new WebUiOidcOptions { Enabled = false, Authority = "https://id.example.com" };
		Assert.True(Validator.Validate(null, options).Succeeded);
	}
}
