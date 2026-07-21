using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Server;

namespace ActiveSync.Server.Tests;

public class StartupSummaryRedactionTests
{
	[Fact]
	public void DescribeUser_NeverRendersPasswordValues()
	{
		AccountOptions options = new()
		{
			Password = "plain-gw-secret",
			MailAddress = "alice@example.com",
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new()
				{
					UserName = "imapuser", Password = "enc:v1:AAAAsealedvalue",
					Settings = new Dictionary<string, string?> { ["Host"] = "imap.example.com", ["Port"] = "993" },
				},
				["MailSubmit"] = new() { Password = "plain-smtp-secret" },
				["Calendar"] = new() { Enabled = false },
				["Contacts"] = new()
				{
					Provider = "carddav", Password = "plain-dav-secret",
					Settings = new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com" },
				},
			},
		};

		string line = StartupSummary.DescribeUser(new MergedAccount(options, true, true));

		Assert.DoesNotContain("plain-gw-secret", line);
		Assert.DoesNotContain("sealedvalue", line);
		Assert.DoesNotContain("plain-smtp-secret", line);
		Assert.DoesNotContain("plain-dav-secret", line);
		Assert.Contains("password=***(PLAINTEXT)", line);
		Assert.Contains("pw=***(sealed)", line);
		Assert.Contains("pw=***(PLAINTEXT)", line);
		Assert.Contains("[db, shadows config]", line);
		Assert.Contains("mail=alice@example.com", line);
		Assert.Contains("Host=imap.example.com", line);
		Assert.Contains("calendar[off]", line);
		Assert.Contains("BaseUrl=https://dav.example.com", line);
	}

	[Fact]
	public void DescribeUser_MasksSecretNamedRoleSettings()
	{
		// E15: the per-role Settings loop printed every setting verbatim; only Password was masked,
		// so an ApiKey/Token in a role override leaked in full into the banner (and the DB log sink).
		AccountOptions options = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["Calendar"] = new()
				{
					Provider = "carddav",
					Settings = new Dictionary<string, string?>
					{
						["ApiKey"] = "banner-api-secret",
						["OAuthToken"] = "banner-token-secret",
						["BaseUrl"] = "https://dav.example.com",
					},
				},
			},
		};

		string line = StartupSummary.DescribeUser(new MergedAccount(options, false, false));

		Assert.DoesNotContain("banner-api-secret", line);
		Assert.DoesNotContain("banner-token-secret", line);
		Assert.Contains("ApiKey=***", line);
		Assert.Contains("OAuthToken=***", line);
		// Non-secret settings still render for diagnostics.
		Assert.Contains("BaseUrl=https://dav.example.com", line);
	}

	[Fact]
	public void DescribeUser_HashedPassword_AndGrantEntry()
	{
		string hashed = ActiveSync.Core.Security.GatewayPasswordHasher.Hash("secret1");
		string withHash = StartupSummary.DescribeUser(
			new MergedAccount(new AccountOptions { Password = hashed }, false, false));
		Assert.DoesNotContain("secret1", withHash);
		Assert.DoesNotContain(hashed[10..30], withHash);
		Assert.Contains("password=***(pbkdf2)", withHash);
		Assert.Contains("[config]", withHash);

		string grant = StartupSummary.DescribeUser(new MergedAccount(new AccountOptions(), true, false));
		Assert.Contains("[db]", grant);
		Assert.Contains("allowlist grant", grant);
	}

	[Fact]
	public void PostgresUri_UserInfoPassword_IsMasked()
	{
		string redacted = StartupSummary.Redact("Postgres",
			"postgresql://activesync:hunter2@active-sync-db-rw.active-sync:5432/activesync");
		Assert.DoesNotContain("hunter2", redacted);
		Assert.Equal("postgresql://activesync:***@active-sync-db-rw.active-sync:5432/activesync", redacted);
	}

	[Fact]
	public void JdbcUri_QueryPassword_IsMasked()
	{
		string redacted = StartupSummary.Redact("Postgres",
			"jdbc:postgresql://active-sync-db-rw.active-sync:5432/activesync?password=hunter2&user=activesync");
		Assert.DoesNotContain("hunter2", redacted);
		Assert.Contains("password=***", redacted);
		Assert.Contains("user=activesync", redacted);
	}

	[Fact]
	public void KeywordForm_PasswordValue_IsMasked()
	{
		string redacted = StartupSummary.Redact("Postgres",
			"Host=db;Database=activesync;Username=u;Password=hunter2;Pooling=true");
		Assert.DoesNotContain("hunter2", redacted);
		Assert.Contains("Password=***", redacted);
		Assert.Contains("Pooling=true", redacted);
	}

	[Fact]
	public void Sqlite_FilePath_IsUntouched()
	{
		Assert.Equal("Data Source=/data/activesync.db",
			StartupSummary.Redact("Sqlite", "Data Source=/data/activesync.db"));
	}
}
