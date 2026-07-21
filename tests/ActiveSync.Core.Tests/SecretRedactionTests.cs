using ActiveSync.Core.Administration;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The unified redactor (S7 / K37): one notion of what is secret and one connection-string
///   redactor, so the CLI, the startup banner, the web settings/backends editors and the
///   per-account views can no longer disagree about what to hide.
/// </summary>
public class SecretRedactionTests
{
	[Theory]
	[InlineData("Password")]
	[InlineData("Pwd")]
	[InlineData("Passphrase")]
	[InlineData("ApiKey")]
	[InlineData("Token")]
	[InlineData("OAuthToken")]
	[InlineData("ClientSecret")]
	[InlineData("Credential")]
	[InlineData("ActiveSync:Backends:MailStore:ApiKey")]
	public void IsSecretName_FlagsEverySecretShape(string name)
	{
		Assert.True(SecretRedaction.IsSecretName(name));
	}

	[Theory]
	[InlineData("Host")]
	[InlineData("Port")]
	[InlineData("UseSsl")]
	[InlineData("BaseUrl")]
	[InlineData("UserName")]
	[InlineData("CertificateKeyPath")] // a file path, not a secret — bare "key" must not trip it
	[InlineData("CaCertificatePath")]
	[InlineData("TaskFolder")]
	[InlineData("")]
	public void IsSecretName_LeavesNonSecretsAlone(string name)
	{
		Assert.False(SecretRedaction.IsSecretName(name));
	}

	[Fact]
	public void MaskIfSecret_MasksSecretsAndPassesTheRestThrough()
	{
		Assert.Equal(SecretRedaction.Mask, SecretRedaction.MaskIfSecret("ApiKey", "abc123"));
		Assert.Equal("imap.example.com", SecretRedaction.MaskIfSecret("Host", "imap.example.com"));
		Assert.Null(SecretRedaction.MaskIfSecret("Password", null));
	}

	[Fact]
	public void RedactConnectionString_MasksPostgresUriUserInfoAndQuery()
	{
		string redacted = SecretRedaction.RedactConnectionString(
			"postgresql://activesync:hunter2@db.internal:5432/activesync");
		Assert.DoesNotContain("hunter2", redacted);
		Assert.Equal("postgresql://activesync:***@db.internal:5432/activesync", redacted);

		string jdbc = SecretRedaction.RedactConnectionString(
			"jdbc:postgresql://db.internal:5432/activesync?password=hunter2&user=activesync");
		Assert.DoesNotContain("hunter2", jdbc);
		Assert.Contains("password=***", jdbc);
		Assert.Contains("user=activesync", jdbc);
	}

	[Fact]
	public void RedactConnectionString_MasksKeywordFormForEveryProvider()
	{
		Assert.Contains("Password=***", SecretRedaction.RedactConnectionString(
			"Host=db;Username=u;Password=hunter2;Pooling=true"));

		// E23: a SQLite/SQLCipher string carrying a Password keyword is masked, not waved through.
		string sqlite = SecretRedaction.RedactConnectionString("Data Source=/data/app.db;Password=cipherkey");
		Assert.DoesNotContain("cipherkey", sqlite);
		Assert.Contains("Password=***", sqlite);

		// A password-free file path is untouched.
		Assert.Equal("Data Source=/data/app.db",
			SecretRedaction.RedactConnectionString("Data Source=/data/app.db"));
	}
}
