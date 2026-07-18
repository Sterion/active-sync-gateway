using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;

namespace ActiveSync.Core.Tests;

public class AccountResolverTests
{
	private static ActiveSyncOptions BaseOptions()
	{
		return new ActiveSyncOptions
		{
			Imap = new ImapOptions { Host = "imap.global", Port = 143, UseSsl = false, Security = "None" },
			Smtp = new SmtpOptions { Host = "smtp.global", Port = 587, UseSsl = false },
			Encryption = new EncryptionOptions { AllowPlaintext = true }
		};
	}

	private static AccountResolver Resolver(ActiveSyncOptions options)
	{
		return new AccountResolver(Microsoft.Extensions.Options.Options.Create(options));
	}

	// ---------- pass-through baseline ----------

	[Fact]
	public void UndeclaredLogin_IsPurePassThrough()
	{
		ActiveSyncOptions options = BaseOptions();
		options.CalDav = new DavServerOptions { BaseUrl = "https://dav.global" };
		AccountResolver resolver = Resolver(options);

		BackendCredentials presented = new("user1@example.com", "pass");
		ResolvedAccount account = resolver.Resolve(presented);
		Assert.Equal("user1@example.com", account.GatewayLogin);
		Assert.Equal("user1@example.com", account.MailAddress); // login contains '@'
		Assert.False(account.MailAddressIsExplicit);
		Assert.Same(options.Imap, account.Imap.Options); // globals pass through untouched
		Assert.Equal(presented, account.Imap.Credentials);
		Assert.Equal(presented, account.Smtp.Credentials);
		Assert.NotNull(account.CalDav);
		Assert.Equal(presented, account.CalDav.Credentials);
		Assert.Null(account.CardDav);
		Assert.Null(resolver.Resolve(new BackendCredentials("justauser", "x")).MailAddress);
		// No local auth rule for undeclared logins → caller must probe IMAP.
		Assert.Null(resolver.VerifyLocally("user1@example.com", "pass"));
	}

	[Fact]
	public void DeclaredEmptyEntry_BehavesLikePassThrough()
	{
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, AccountOptions> { ["user1@example.com"] = new() };
		AccountResolver resolver = Resolver(options);

		BackendCredentials presented = new("user1@example.com", "pass");
		ResolvedAccount account = resolver.Resolve(presented);
		Assert.Equal(presented, account.Imap.Credentials);
		Assert.Equal(presented, account.Smtp.Credentials);
		Assert.Equal("imap.global", account.Imap.Options.Host);
		Assert.Null(resolver.VerifyLocally("user1@example.com", "pass")); // still probes
	}

	// ---------- override + inheritance matrix ----------

	[Fact]
	public void FieldOverrides_Win_UnsetFieldsInherit()
	{
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["u"] = new()
			{
				Imap = new ImapAccountOptions { Host = "imap.other", UseSsl = true, PathSeparator = '/' },
				Smtp = new SmtpAccountOptions
				{
					UserName = "relay-user", Password = "relay-pw", Port = 2525, ForceFrom = true
				}
			}
		};

		ResolvedAccount account = Resolver(options).Resolve(new BackendCredentials("u", "presented-pw"));
		Assert.Equal("imap.other", account.Imap.Options.Host);   // overridden
		Assert.Equal(143, account.Imap.Options.Port);            // inherited
		Assert.True(account.Imap.Options.UseSsl);                // overridden
		Assert.Equal('/', account.Imap.Options.PathSeparator);   // overridden
		Assert.Equal(new BackendCredentials("u", "presented-pw"), account.Imap.Credentials);
		Assert.Equal(new BackendCredentials("relay-user", "relay-pw"), account.Smtp.Credentials);
		Assert.Equal("smtp.global", account.Smtp.Options.Host);  // inherited
		Assert.Equal(2525, account.Smtp.Options.Port);           // overridden
		Assert.True(account.Smtp.Options.ForceFrom);             // overridden
	}

	[Fact]
	public void PasswordInheritance_PresentedFlowsThroughTheChain()
	{
		ActiveSyncOptions options = BaseOptions();
		options.CalDav = new DavServerOptions { BaseUrl = "https://dav.global" };
		options.Users = new Dictionary<string, AccountOptions>
		{
			// Only the IMAP user name differs; every password inherits the presented one.
			["phone"] = new() { Imap = new ImapAccountOptions { UserName = "mailbox@example.com" } }
		};

		ResolvedAccount account = Resolver(options).Resolve(new BackendCredentials("phone", "P"));
		Assert.Equal(new BackendCredentials("mailbox@example.com", "P"), account.Imap.Credentials);
		Assert.Equal(account.Imap.Credentials, account.Smtp.Credentials); // SMTP ← effective IMAP
		Assert.NotNull(account.CalDav);
		Assert.Equal(account.Imap.Credentials, account.CalDav.Credentials); // DAV ← effective IMAP
	}

	[Fact]
	public void ConfiguredImapPassword_OverridesPresented_ForAllInheritors()
	{
		ActiveSyncOptions options = BaseOptions();
		options.CalDav = new DavServerOptions { BaseUrl = "https://dav.global" };
		options.Users = new Dictionary<string, AccountOptions>
		{
			["phone"] = new() { Imap = new ImapAccountOptions { Password = "imap-pw" } }
		};

		ResolvedAccount account = Resolver(options).Resolve(new BackendCredentials("phone", "imap-pw"));
		Assert.Equal("imap-pw", account.Imap.Credentials.Password);
		Assert.Equal("imap-pw", account.Smtp.Credentials.Password);
		Assert.Equal("imap-pw", account.CalDav!.Credentials.Password);
	}

	[Fact]
	public void DavInheritance_DisableSwitch_AndPerUserOnlyDav()
	{
		ActiveSyncOptions options = BaseOptions();
		options.CalDav = new DavServerOptions { BaseUrl = "https://dav.global", HomeSetPath = "/{user}/" };
		options.Users = new Dictionary<string, AccountOptions>
		{
			["inherits"] = new(),
			["disables"] = new() { CalDav = new DavAccountOptions { Enabled = false } },
			["own-carddav"] = new()
			{
				CardDav = new DavAccountOptions
				{
					BaseUrl = "https://cloud.example.com", UserName = "nc-user", Password = "nc-pw"
				}
			}
		};
		AccountResolver resolver = Resolver(options);
		BackendCredentials presented = new("x", "P");

		ResolvedAccount inherits = resolver.Resolve(presented with { UserName = "inherits" });
		Assert.NotNull(inherits.CalDav);
		Assert.Equal("https://dav.global", inherits.CalDav.Options.BaseUrl);
		Assert.Equal("Tasks", inherits.CalDav.Options.TaskFolder); // section default flows
		Assert.Equal("P", inherits.CalDav.Credentials.Password);   // presented inherited
		Assert.Null(inherits.CardDav);

		Assert.Null(resolver.Resolve(presented with { UserName = "disables" }).CalDav);

		ResolvedAccount ownCardDav = resolver.Resolve(presented with { UserName = "own-carddav" });
		Assert.NotNull(ownCardDav.CardDav); // enabled by the user's own BaseUrl alone
		Assert.Equal(new BackendCredentials("nc-user", "nc-pw"), ownCardDav.CardDav.Credentials);
	}

	[Fact]
	public void MailAddress_IsExplicitFlag_AndNeverChangesImapUserName()
	{
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["phone"] = new() { MailAddress = "real@example.com" }
		};

		ResolvedAccount account = Resolver(options).Resolve(new BackendCredentials("phone", "P"));
		Assert.Equal("real@example.com", account.MailAddress);
		Assert.True(account.MailAddressIsExplicit);
		Assert.Equal("phone", account.Imap.Credentials.UserName); // login, NOT the mail address
	}

	[Fact]
	public void SealedImapPassword_IsUnsealed_AndComparableForAuth()
	{
		byte[] key = new byte[32];
		Array.Fill(key, (byte)9);
		ActiveSyncOptions options = BaseOptions();
		options.Encryption = new EncryptionOptions { Key = Convert.ToBase64String(key) };
		options.Users = new Dictionary<string, AccountOptions>
		{
			["u"] = new() { Imap = new ImapAccountOptions { Password = SecretValue.Seal("real-imap-pw", key) } }
		};
		AccountResolver resolver = Resolver(options);

		Assert.Equal("real-imap-pw",
			resolver.Resolve(new BackendCredentials("u", "ignored")).Imap.Credentials.Password);
		Assert.True(resolver.VerifyLocally("u", "real-imap-pw"));
		Assert.False(resolver.VerifyLocally("u", "wrong"));
	}

	// ---------- local auth rules ----------

	[Fact]
	public void VerifyLocally_PrecedenceMatrix()
	{
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["hashed"] = new() { Password = GatewayPasswordHasher.Hash("gw-secret") },
			["plain"] = new() { Password = "gw-plain" },
			// Gateway Password wins over Imap:Password when both are set.
			["both"] = new()
			{
				Password = "gateway-wins", Imap = new ImapAccountOptions { Password = "imap-pw" }
			},
			["imap-pinned"] = new() { Imap = new ImapAccountOptions { Password = "imap-pw" } },
			["probe-me"] = new() { Imap = new ImapAccountOptions { UserName = "other" } }
		};
		AccountResolver resolver = Resolver(options);

		Assert.True(resolver.VerifyLocally("hashed", "gw-secret"));
		Assert.False(resolver.VerifyLocally("hashed", "wrong"));
		Assert.True(resolver.VerifyLocally("plain", "gw-plain"));
		Assert.True(resolver.VerifyLocally("both", "gateway-wins"));
		Assert.False(resolver.VerifyLocally("both", "imap-pw")); // backend pw is not the phone pw
		Assert.True(resolver.VerifyLocally("imap-pinned", "imap-pw"));
		Assert.False(resolver.VerifyLocally("imap-pinned", "nope"));
		Assert.Null(resolver.VerifyLocally("probe-me", "anything")); // no local rule → probe
		Assert.Null(resolver.VerifyLocally("undeclared", "anything"));
		// Case-insensitive lookup.
		Assert.True(resolver.VerifyLocally("PLAIN", "gw-plain"));
	}

	[Fact]
	public void RequireDeclaredUsers_RejectsUndeclared_WithoutProbing()
	{
		ActiveSyncOptions options = BaseOptions();
		options.RequireDeclaredUsers = true;
		options.Users = new Dictionary<string, AccountOptions> { ["allowed"] = new() };
		AccountResolver resolver = Resolver(options);

		Assert.False(resolver.VerifyLocally("stranger", "any"));   // definitive local reject
		Assert.Null(resolver.VerifyLocally("allowed", "any"));     // empty entry → normal probe
	}

	// ---------- validation ----------

	[Fact]
	public void ValidateUsers_ReportsBadLogin_EmptyHost_AndInvalidDavUrl()
	{
		ActiveSyncOptions options = BaseOptions();
		options.Imap.Host = "";
		options.Users = new Dictionary<string, AccountOptions>
		{
			["bad\nlogin"] = new() { CalDav = new DavAccountOptions { BaseUrl = "not-a-url" } }
		};

		List<string> failures = new();
		AccountResolver.ValidateUsers(options, null, failures);
		string joined = string.Join(";", failures);
		Assert.Contains("control characters", joined);
		Assert.Contains("effective Imap:Host is empty", joined);
		Assert.Contains("CalDav:BaseUrl 'not-a-url'", joined);
	}

	[Fact]
	public void ValidateUsers_SealedValueWithoutKey_IsReported()
	{
		byte[] key = new byte[32];
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["u"] = new() { Imap = new ImapAccountOptions { Password = SecretValue.Seal("pw", key) } }
		};

		List<string> failures = new();
		AccountResolver.ValidateUsers(options, null, failures);
		Assert.Contains("sealed (enc:v1:) but no ActiveSync:Encryption key", string.Join(";", failures));
	}
}
