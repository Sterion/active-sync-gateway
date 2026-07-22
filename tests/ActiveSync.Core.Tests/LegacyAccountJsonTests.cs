using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The pre-role-model row upgrader: legacy imap/calDav/... sections become role-keyed
///   overrides. Critical because System.Text.Json silently drops unknown members — an
///   unconverted row would lose its overrides (an authentication hazard).
/// </summary>
public sealed class LegacyAccountJsonTests
{
	private static AccountOptions Convert(string json)
	{
		string? converted = LegacyAccountJson.TryConvert(json, out string? error);
		Assert.Null(error);
		Assert.NotNull(converted);
		return JsonSerializer.Deserialize<AccountOptions>(converted, AccountStore.JsonOptions)!;
	}

	[Fact]
	public void RoleShapedRow_IsLeftAlone()
	{
		Assert.Null(LegacyAccountJson.TryConvert(
			"""{"mailAddress":"a@x","backends":{"MailStore":{"userName":"u"}}}""", out string? error));
		Assert.Null(error);
		Assert.Null(LegacyAccountJson.TryConvert("{}", out error));
		Assert.Null(error);
	}

	[Fact]
	public void BrokenJson_ReportsInsteadOfThrowing()
	{
		Assert.Null(LegacyAccountJson.TryConvert("{not json", out string? error));
		Assert.NotNull(error);
	}

	[Fact]
	public void ImapAndSmtp_BecomeMailRoles_WithSettings()
	{
		AccountOptions converted = Convert("""
			{"password":"pbkdf2$x","mailAddress":"a@x",
			 "imap":{"userName":"iu","password":"ip","host":"h","port":1143,"useSsl":false,"pathSeparator":"/"},
			 "smtp":{"port":2525,"forceFrom":true}}
			""");
		Assert.Equal("pbkdf2$x", converted.Password);
		Assert.Equal("a@x", converted.MailAddress);
		BackendRoleOverride mail = converted.Backends!["MailStore"];
		Assert.Equal("iu", mail.UserName);
		Assert.Equal("ip", mail.Password);
		Assert.Equal("h", mail.Settings!["host"]);
		Assert.Equal("1143", mail.Settings["port"]);
		Assert.Equal("false", mail.Settings["useSsl"]);
		Assert.Equal("/", mail.Settings["pathSeparator"]);
		BackendRoleOverride submit = converted.Backends["MailSubmit"];
		Assert.Equal("2525", submit.Settings!["port"]);
		Assert.Equal("true", submit.Settings["forceFrom"]);
		Assert.Null(submit.Provider);
	}

	[Fact]
	public void SelfContainedCalDav_SwitchesProvider_AndDuplicatesToTasks()
	{
		AccountOptions converted = Convert("""
			{"calDav":{"baseUrl":"https://dav.x","userName":"du",
			           "sharedCollections":["/a/","/b/|ro"]}}
			""");
		BackendRoleOverride calendar = converted.Backends!["Calendar"];
		Assert.Equal("caldav", calendar.Provider); // own BaseUrl = explicit provider switch
		Assert.Equal("https://dav.x", calendar.Settings!["baseUrl"]);
		Assert.Equal("/a/", calendar.Settings["sharedCollections:0"]);
		Assert.Equal("/b/|ro", calendar.Settings["sharedCollections:1"]);
		// Per-role merges no longer flow Calendar → Tasks, so Tasks carries the same
		// override (without forcing the provider — the global Tasks assignment decides).
		BackendRoleOverride tasks = converted.Backends["Tasks"];
		Assert.Null(tasks.Provider);
		Assert.Equal("du", tasks.UserName);
		Assert.Equal("https://dav.x", tasks.Settings!["baseUrl"]);
	}

	[Fact]
	public void CredentialOnlyCalDav_DoesNotForceTheProvider()
	{
		AccountOptions converted = Convert("""{"calDav":{"userName":"du","password":"dp"}}""");
		BackendRoleOverride calendar = converted.Backends!["Calendar"];
		Assert.Null(calendar.Provider); // follows the global Calendar assignment
		Assert.Equal("du", calendar.UserName);
		Assert.Equal("dp", calendar.Password);
	}

	[Fact]
	public void RootLevelAccountFields_SurviveTheUpgrade()
	{
		// B13: the old converter was a field whitelist (Password + MailAddress + Backends), so it
		// silently DROPPED Admin/Enabled/AutoProvisioned/OidcSubject — a disabled row came back
		// ENABLED after the in-place upgrade, with no log line.
		AccountOptions converted = Convert("""
			{"enabled":false,"admin":true,"autoProvisioned":true,"oidcSubject":"sub-123",
			 "imap":{"host":"h"}}
			""");
		Assert.False(converted.Enabled);
		Assert.True(converted.Admin);
		Assert.True(converted.AutoProvisioned);
		Assert.Equal("sub-123", converted.OidcSubject);
		Assert.NotNull(converted.Backends!["MailStore"]); // legacy section still converts
	}

	[Fact]
	public void DisabledSections_AndSieveOptIn_MapToTheNewSwitches()
	{
		AccountOptions converted = Convert("""
			{"calDav":{"enabled":false,"userName":"ignored"},
			 "sieve":{"enabled":true,"host":"sieve.x"}}
			""");
		Assert.False(converted.Backends!["Calendar"].Enabled);
		Assert.Null(converted.Backends["Calendar"].UserName); // a disabled role keeps nothing
		Assert.False(converted.Backends["Tasks"].Enabled);    // old rule: no caldav → local tasks
		BackendRoleOverride oof = converted.Backends["Oof"];
		Assert.Equal("sieve", oof.Provider); // enabled:true was the per-user opt-in
		Assert.Equal("sieve.x", oof.Settings!["host"]);
	}
}
