using ActiveSync.Backends.Dav;
using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Local;
using ActiveSync.Backends.Sieve;
using ActiveSync.Backends.Smtp;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

public class AccountResolverTests
{
	private static Dictionary<string, string?> BaseConfig()
	{
		return new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.global",
			["ActiveSync:Backends:MailStore:Port"] = "143",
			["ActiveSync:Backends:MailStore:UseSsl"] = "false",
			["ActiveSync:Backends:MailStore:Security"] = "None",
			["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
			["ActiveSync:Backends:MailSubmit:Host"] = "smtp.global",
			["ActiveSync:Backends:MailSubmit:Port"] = "587",
			["ActiveSync:Backends:MailSubmit:UseSsl"] = "false"
		};
	}

	private static ActiveSyncOptions HostOptions()
	{
		return new ActiveSyncOptions { Encryption = new EncryptionOptions { AllowPlaintext = true } };
	}

	private static BackendProviderRegistry Registry()
	{
		return new BackendProviderRegistry(
		[
			new ImapBackendProvider(
				TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new SmtpBackendProvider(NullLoggerFactory.Instance),
			new CalDavBackendProvider(NullLoggerFactory.Instance),
			new CardDavBackendProvider(NullLoggerFactory.Instance),
			new SieveBackendProvider(NullLoggerFactory.Instance),
			// Only ValidateConfiguration/DescribeRole are exercised here — no connections.
			new LocalBackendProvider(null!, null!, null!)
		], NullLogger<BackendProviderRegistry>.Instance);
	}

	private static BackendRolesConfig Roles(Dictionary<string, string?> config)
	{
		IConfigurationRoot root = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
		List<string> failures = new();
		BackendRolesConfig roles = BackendRolesConfig.Load(root, failures);
		Assert.Empty(failures);
		return roles;
	}

	private static AccountResolver Resolver(ActiveSyncOptions options, Dictionary<string, string?> config)
	{
		return new AccountResolver(
			TestOptionsMonitor.Of(options), Roles(config), Registry());
	}

	// ---------- pass-through baseline ----------

	[Fact]
	public void UndeclaredLogin_IsPurePassThrough()
	{
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:Calendar:Provider"] = "caldav";
		config["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global";
		AccountResolver resolver = Resolver(HostOptions(), config);

		BackendCredentials presented = new("user1@example.com", "pass");
		ResolvedAccount account = resolver.Resolve(presented);
		Assert.Equal("user1@example.com", account.GatewayLogin);
		Assert.Equal("user1@example.com", account.MailAddress); // login contains '@'
		Assert.False(account.MailAddressIsExplicit);
		ResolvedRole mailStore = account.Roles[BackendRole.MailStore];
		Assert.Equal("imap", mailStore.ProviderName);
		Assert.Equal("imap.global", mailStore.Settings.Bind<ImapOptions>().Host);
		Assert.Equal(presented, mailStore.Credentials);
		Assert.Equal(presented, account.Roles[BackendRole.MailSubmit].Credentials);
		Assert.Equal("caldav", account.Roles[BackendRole.Calendar].ProviderName);
		Assert.Equal(presented, account.Roles[BackendRole.Calendar].Credentials);
		Assert.Equal("local", account.Roles[BackendRole.Contacts].ProviderName); // fallback
		Assert.Equal("local", account.Roles[BackendRole.Notes].ProviderName);
		Assert.False(account.Roles.ContainsKey(BackendRole.Oof)); // absent = feature off
		Assert.Null(resolver.Resolve(new BackendCredentials("justauser", "x")).MailAddress);
		// No local auth rule for undeclared logins → caller must probe the mail backend.
		Assert.Null(resolver.VerifyLocally("user1@example.com", "pass"));
	}

	[Fact]
	public void DeclaredEmptyEntry_BehavesLikePassThrough()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions> { ["user1@example.com"] = new() };
		AccountResolver resolver = Resolver(options, BaseConfig());

		BackendCredentials presented = new("user1@example.com", "pass");
		ResolvedAccount account = resolver.Resolve(presented);
		Assert.Equal(presented, account.Roles[BackendRole.MailStore].Credentials);
		Assert.Equal(presented, account.Roles[BackendRole.MailSubmit].Credentials);
		Assert.Equal("imap.global", account.Roles[BackendRole.MailStore].Settings.Bind<ImapOptions>().Host);
		Assert.Null(resolver.VerifyLocally("user1@example.com", "pass")); // still probes
	}

	// ---------- override + inheritance matrix ----------

	[Fact]
	public void SettingOverrides_Win_UnsetKeysInherit()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["u"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new()
					{
						Settings = new Dictionary<string, string?>
							{ ["Host"] = "imap.other", ["UseSsl"] = "true", ["PathSeparator"] = "/" }
					},
					["MailSubmit"] = new()
					{
						UserName = "relay-user", Password = "relay-pw",
						Settings = new Dictionary<string, string?> { ["Port"] = "2525", ["ForceFrom"] = "true" }
					}
				}
			}
		};

		ResolvedAccount account = Resolver(options, BaseConfig())
			.Resolve(new BackendCredentials("u", "presented-pw"));
		ImapOptions imap = account.Roles[BackendRole.MailStore].Settings.Bind<ImapOptions>();
		Assert.Equal("imap.other", imap.Host);  // overridden
		Assert.Equal(143, imap.Port);           // inherited
		Assert.True(imap.UseSsl);               // overridden
		Assert.Equal('/', imap.PathSeparator);  // overridden
		Assert.Equal(new BackendCredentials("u", "presented-pw"),
			account.Roles[BackendRole.MailStore].Credentials);
		Assert.Equal(new BackendCredentials("relay-user", "relay-pw"),
			account.Roles[BackendRole.MailSubmit].Credentials);
		SmtpOptions smtp = account.Roles[BackendRole.MailSubmit].Settings.Bind<SmtpOptions>();
		Assert.Equal("smtp.global", smtp.Host); // inherited
		Assert.Equal(2525, smtp.Port);          // overridden
		Assert.True(smtp.ForceFrom);            // overridden
	}

	[Fact]
	public void PasswordInheritance_PresentedFlowsThroughTheChain()
	{
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:Calendar:Provider"] = "caldav";
		config["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global";
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			// Only the mail user name differs; every password inherits the presented one.
			["phone"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { UserName = "mailbox@example.com" } }
			}
		};

		ResolvedAccount account = Resolver(options, config).Resolve(new BackendCredentials("phone", "P"));
		BackendCredentials mail = account.Roles[BackendRole.MailStore].Credentials;
		Assert.Equal(new BackendCredentials("mailbox@example.com", "P"), mail);
		Assert.Equal(mail, account.Roles[BackendRole.MailSubmit].Credentials); // submit ← effective mail
		Assert.Equal(mail, account.Roles[BackendRole.Calendar].Credentials);   // DAV ← effective mail
	}

	[Fact]
	public void ConfiguredMailStorePassword_OverridesPresented_ForAllInheritors()
	{
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:Calendar:Provider"] = "caldav";
		config["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global";
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["phone"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = "mail-pw" } }
			}
		};

		ResolvedAccount account = Resolver(options, config).Resolve(new BackendCredentials("phone", "mail-pw"));
		Assert.Equal("mail-pw", account.Roles[BackendRole.MailStore].Credentials.Password);
		Assert.Equal("mail-pw", account.Roles[BackendRole.MailSubmit].Credentials.Password);
		Assert.Equal("mail-pw", account.Roles[BackendRole.Calendar].Credentials.Password);
	}

	[Fact]
	public void ListOverride_ReplacesTheWholeGlobalList()
	{
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:Calendar:Provider"] = "caldav";
		config["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global";
		config["ActiveSync:Backends:Calendar:SharedCollections:0"] = "/cal/global/";
		config["ActiveSync:Backends:Calendar:SharedCollections:1"] = "/cal/other/|ro";
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["inherits"] = new(),
			["replaces"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["Calendar"] = new()
					{
						Settings = new Dictionary<string, string?> { ["SharedCollections:0"] = "/cal/own/" }
					}
				}
			}
		};
		AccountResolver resolver = Resolver(options, config);
		BackendCredentials presented = new("x", "P");

		DavServerOptions inherited = resolver.Resolve(presented with { UserName = "inherits" })
			.Roles[BackendRole.Calendar].Settings.Bind<DavServerOptions>();
		Assert.Equal(["/cal/global/", "/cal/other/|ro"], inherited.SharedCollections);

		// A user list REPLACES the global one — a shorter override must not inherit the
		// global tail elements (the subtree-replace merge rule).
		DavServerOptions replaced = resolver.Resolve(presented with { UserName = "replaces" })
			.Roles[BackendRole.Calendar].Settings.Bind<DavServerOptions>();
		Assert.Equal(["/cal/own/"], replaced.SharedCollections);
	}

	[Fact]
	public void RoleDisable_ProviderSwitch_AndPerUserOnlyBackend()
	{
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:Calendar:Provider"] = "caldav";
		config["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global";
		config["ActiveSync:Backends:Calendar:HomeSetPath"] = "/{user}/";
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["inherits"] = new(),
			["disables"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["Calendar"] = new() { Enabled = false } }
			},
			["own-carddav"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["Contacts"] = new()
					{
						Provider = "carddav", UserName = "nc-user", Password = "nc-pw",
						Settings = new Dictionary<string, string?>
							{ ["BaseUrl"] = "https://cloud.example.com" }
					}
				}
			}
		};
		AccountResolver resolver = Resolver(options, config);
		BackendCredentials presented = new("x", "P");

		ResolvedRole inherits = resolver.Resolve(presented with { UserName = "inherits" })
			.Roles[BackendRole.Calendar];
		Assert.Equal("caldav", inherits.ProviderName);
		DavServerOptions dav = inherits.Settings.Bind<DavServerOptions>();
		Assert.Equal("https://dav.global", dav.BaseUrl);
		Assert.Equal("Tasks", dav.TaskFolder);          // option-class default flows
		Assert.Equal("P", inherits.Credentials.Password); // presented inherited

		Assert.Equal("local", resolver.Resolve(presented with { UserName = "disables" })
			.Roles[BackendRole.Calendar].ProviderName);

		ResolvedRole contacts = resolver.Resolve(presented with { UserName = "own-carddav" })
			.Roles[BackendRole.Contacts];
		Assert.Equal("carddav", contacts.ProviderName); // switched per user
		Assert.Equal("https://cloud.example.com", contacts.Settings.Bind<DavServerOptions>().BaseUrl);
		Assert.Equal(new BackendCredentials("nc-user", "nc-pw"), contacts.Credentials);
	}

	[Fact]
	public void MailAddress_IsExplicitFlag_AndNeverChangesMailUserName()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["phone"] = new() { MailAddress = "real@example.com" }
		};

		ResolvedAccount account = Resolver(options, BaseConfig()).Resolve(new BackendCredentials("phone", "P"));
		Assert.Equal("real@example.com", account.MailAddress);
		Assert.True(account.MailAddressIsExplicit);
		// login, NOT the mail address
		Assert.Equal("phone", account.Roles[BackendRole.MailStore].Credentials.UserName);
	}

	[Fact]
	public void SealedMailStorePassword_IsUnsealed_AndComparableForAuth()
	{
		byte[] key = new byte[32];
		Array.Fill(key, (byte)9);
		ActiveSyncOptions options = HostOptions();
		options.Encryption = new EncryptionOptions { Key = Convert.ToBase64String(key) };
		options.Users = new Dictionary<string, AccountOptions>
		{
			["u"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = SecretValue.Seal("real-mail-pw", key) } }
			}
		};
		AccountResolver resolver = Resolver(options, BaseConfig());

		Assert.Equal("real-mail-pw", resolver.Resolve(new BackendCredentials("u", "ignored"))
			.Roles[BackendRole.MailStore].Credentials.Password);
		Assert.True(resolver.VerifyLocally("u", "real-mail-pw"));
		Assert.False(resolver.VerifyLocally("u", "wrong"));
	}

	// ---------- local auth rules ----------

	[Fact]
	public void VerifyLocally_PrecedenceMatrix()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["hashed"] = new() { Password = GatewayPasswordHasher.Hash("gw-secret") },
			["plain"] = new() { Password = "gw-plain" },
			// Gateway Password wins over the MailStore password when both are set.
			["both"] = new()
			{
				Password = "gateway-wins",
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = "mail-pw" } }
			},
			["mail-pinned"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = "mail-pw" } }
			},
			["probe-me"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { UserName = "other" } }
			}
		};
		AccountResolver resolver = Resolver(options, BaseConfig());

		Assert.True(resolver.VerifyLocally("hashed", "gw-secret"));
		Assert.False(resolver.VerifyLocally("hashed", "wrong"));
		Assert.True(resolver.VerifyLocally("plain", "gw-plain"));
		Assert.True(resolver.VerifyLocally("both", "gateway-wins"));
		Assert.False(resolver.VerifyLocally("both", "mail-pw")); // backend pw is not the phone pw
		Assert.True(resolver.VerifyLocally("mail-pinned", "mail-pw"));
		Assert.False(resolver.VerifyLocally("mail-pinned", "nope"));
		Assert.Null(resolver.VerifyLocally("probe-me", "anything")); // no local rule → probe
		Assert.Null(resolver.VerifyLocally("undeclared", "anything"));
		// Case-insensitive lookup.
		Assert.True(resolver.VerifyLocally("PLAIN", "gw-plain"));
	}

	[Fact]
	public void RequireDeclaredUsers_RejectsUndeclared_WithoutProbing()
	{
		ActiveSyncOptions options = HostOptions();
		options.RequireDeclaredUsers = true;
		options.Users = new Dictionary<string, AccountOptions> { ["allowed"] = new() };
		AccountResolver resolver = Resolver(options, BaseConfig());

		Assert.False(resolver.VerifyLocally("stranger", "any"));   // definitive local reject
		Assert.Null(resolver.VerifyLocally("allowed", "any"));     // empty entry → normal probe
	}

	// ---------- validation ----------

	[Fact]
	public void ValidateUsers_ReportsBadLogin_MissingHost_AndInvalidDavUrl()
	{
		Dictionary<string, string?> config = BaseConfig();
		config.Remove("ActiveSync:Backends:MailStore:Host");
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["bad\nlogin"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["Calendar"] = new()
					{
						Provider = "caldav",
						Settings = new Dictionary<string, string?> { ["BaseUrl"] = "not-a-url" }
					}
				}
			}
		};

		List<string> failures = new();
		AccountResolver.ValidateUsers(options, Roles(config), Registry(), null, failures);
		string joined = string.Join(";", failures);
		Assert.Contains("control characters", joined);
		Assert.Contains("Host is required", joined);
		Assert.Contains("BaseUrl 'not-a-url'", joined);
	}

	[Fact]
	public void ValidateUsers_UnknownRole_AndUnknownProvider_AreReported()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["u"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["Frisbee"] = new(),
					["Calendar"] = new() { Provider = "jmap" }
				}
			}
		};

		List<string> failures = new();
		AccountResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), null, failures);
		string joined = string.Join(";", failures);
		Assert.Contains("Frisbee", joined);
		Assert.Contains("jmap", joined);
	}

	[Fact]
	public void ValidateUsers_MalformedGatewayPasswordHash_IsReported()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions> { ["u"] = new() { Password = "pbkdf2$broken" } };

		List<string> failures = new();
		AccountResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), null, failures);
		Assert.Contains("not a valid pbkdf2$ value", string.Join(";", failures));
	}

	[Fact]
	public void ValidateUsers_SealedValueWithoutKey_IsReported()
	{
		byte[] key = new byte[32];
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["u"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = SecretValue.Seal("pw", key) } }
			}
		};

		List<string> failures = new();
		AccountResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), null, failures);
		Assert.Contains("sealed (enc:v1:) but no ActiveSync:Encryption key", string.Join(";", failures));
	}
}
