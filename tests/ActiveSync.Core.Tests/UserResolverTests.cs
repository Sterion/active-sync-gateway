using ActiveSync.Backends.Dav;
using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Local;
using ActiveSync.Backends.Sieve;
using ActiveSync.Backends.Smtp;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.Settings;
using ActiveSync.Crypto;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

public class UserResolverTests
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
			new CalDavBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new CardDavBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new SieveBackendProvider(NullLoggerFactory.Instance),
			// Only ValidateConfiguration/DescribeRole are exercised here — no connections.
			new LocalBackendProvider(null!, null!, null!, NullLoggerFactory.Instance)
		], NullLogger<BackendProviderRegistry>.Instance);
	}

	private static BackendRolesProvider RolesProvider(Dictionary<string, string?> config)
	{
		IConfigurationRoot root = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
		return new BackendRolesProvider(root);
	}

	private static BackendRolesConfig Roles(Dictionary<string, string?> config)
	{
		IConfigurationRoot root = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
		List<string> failures = new();
		BackendRolesConfig roles = BackendRolesConfig.Load(root, failures);
		Assert.Empty(failures);
		return roles;
	}

	private static UserResolver Resolver(ActiveSyncOptions options, Dictionary<string, string?> config)
	{
		return new UserResolver(
			TestOptionsMonitor.Of(options), RolesProvider(config), Registry());
	}

	// ---------- pass-through baseline ----------

	[Fact]
	public void OrderedRoles_IsStableAndComputedOnce()
	{
		// B28 (item 37): OrderedRoles used to sort+ToList on EVERY read, so each access allocated a
		// fresh list with a different identity. Cache it: repeated reads return the SAME instance
		// (and the order — enum order, MailStore first — is unchanged).
		ResolvedUser account = new(
			"u@x", "u@x", false,
			new Dictionary<BackendRole, ResolvedRole>
			{
				[BackendRole.MailSubmit] =
					new(BackendRole.MailSubmit, "smtp", ProviderSettings.Empty, new BackendCredentials("u", "p")),
				[BackendRole.MailStore] =
					new(BackendRole.MailStore, "imap", ProviderSettings.Empty, new BackendCredentials("u", "p")),
			});

		IReadOnlyList<ResolvedRole> first = account.OrderedRoles;
		IReadOnlyList<ResolvedRole> second = account.OrderedRoles;

		Assert.Same(first, second);
		Assert.Equal(BackendRole.MailStore, first[0].Role); // still sorted, MailStore first
	}

	[Fact]
	public void UndeclaredLogin_IsPurePassThrough()
	{
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:Calendar:Provider"] = "caldav";
		config["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global";
		UserResolver resolver = Resolver(HostOptions(), config);

		BackendCredentials presented = new("user1@example.com", "pass");
		ResolvedUser account = resolver.Resolve(presented);
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
		options.Users = new Dictionary<string, UserOptions> { ["user1@example.com"] = new() };
		UserResolver resolver = Resolver(options, BaseConfig());

		BackendCredentials presented = new("user1@example.com", "pass");
		ResolvedUser account = resolver.Resolve(presented);
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
		options.Users = new Dictionary<string, UserOptions>
		{
			["u"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new()
					{
						Settings = new Dictionary<string, string?>
							{ ["Host"] = "imap.other", ["UseSsl"] = "true", ["Security"] = "StartTls" }
					},
					["MailSubmit"] = new()
					{
						UserName = "relay-user", Password = "relay-pw",
						Settings = new Dictionary<string, string?> { ["Port"] = "2525", ["ForceFrom"] = "true" }
					}
				}
			}
		};

		ResolvedUser account = Resolver(options, BaseConfig())
			.Resolve(new BackendCredentials("u", "presented-pw"));
		ImapOptions imap = account.Roles[BackendRole.MailStore].Settings.Bind<ImapOptions>();
		Assert.Equal("imap.other", imap.Host);  // overridden
		Assert.Equal(143, imap.Port);           // inherited
		Assert.True(imap.UseSsl);               // overridden
		Assert.Equal("StartTls", imap.Security); // overridden
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
		options.Users = new Dictionary<string, UserOptions>
		{
			// Only the mail user name differs; every password inherits the presented one.
			["phone"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { UserName = "mailbox@example.com" } }
			}
		};

		ResolvedUser account = Resolver(options, config).Resolve(new BackendCredentials("phone", "P"));
		BackendCredentials mail = account.Roles[BackendRole.MailStore].Credentials;
		Assert.Equal(new BackendCredentials("mailbox@example.com", "P"), mail);
		// Item 5: MailStore is just another role — the other roles fall back to the user DEFAULTS
		// (unset here, so pass-through), not to the effective MailStore pair.
		Assert.Equal(new BackendCredentials("phone", "P"), account.Roles[BackendRole.MailSubmit].Credentials);
		Assert.Equal(new BackendCredentials("phone", "P"), account.Roles[BackendRole.Calendar].Credentials);
	}

	[Fact]
	public void DefaultBackendCredentials_ApplyToEveryRole_MailStoreIncluded()
	{
		// What replaced MailStore-as-template: ONE explicit pair, applying to every role, which
		// keeps working when the device credential is decoupled from the mail password.
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:Calendar:Provider"] = "caldav";
		config["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global";
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["phone"] = new()
			{
				Password = "phone-pw",                       // device → gateway
				DefaultBackendLogin = "mailbox@example.com", // gateway → backends
				DefaultBackendPassword = "mail-pw",
			}
		};

		ResolvedUser account = Resolver(options, config).Resolve(new BackendCredentials("phone", "phone-pw"));
		foreach (BackendRole role in new[] { BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar })
		{
			Assert.Equal("mailbox@example.com", account.Roles[role].Credentials.UserName);
			Assert.Equal("mail-pw", account.Roles[role].Credentials.Password);
		}
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
		options.Users = new Dictionary<string, UserOptions>
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
		UserResolver resolver = Resolver(options, config);
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
		options.Users = new Dictionary<string, UserOptions>
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
		UserResolver resolver = Resolver(options, config);
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
	public void NullSettingOverride_CLEARS_TheInheritedGlobalKey_NotIgnored()
	{
		// B16 (coverage): the doc used to say "Null values are ignored", but a null user setting
		// actually CLEARS the inherited global key (the removal loop strips the global subtree the
		// key addresses, and the write loop then skips the null). Pin that so the corrected doc and
		// the code cannot drift. If null were ignored, the "clears" user would keep the global value.
		Dictionary<string, string?> config = BaseConfig();
		config["ActiveSync:Backends:MailStore:Security"] = "StartTlsWhenAvailable";
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["inherits"] = new(),
			["clears"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new()
					{
						Settings = new Dictionary<string, string?> { ["Security"] = null },
					},
				},
			},
		};
		UserResolver resolver = Resolver(options, config);
		BackendCredentials presented = new("x", "P");

		// The inheriting user keeps the global security setting; the clearing user drops it (back to
		// the option-class default null) while STILL inheriting the untouched global Host.
		ImapOptions inherited = resolver.Resolve(presented with { UserName = "inherits" })
			.Roles[BackendRole.MailStore].Settings.Bind<ImapOptions>();
		Assert.Equal("StartTlsWhenAvailable", inherited.Security);

		ImapOptions cleared = resolver.Resolve(presented with { UserName = "clears" })
			.Roles[BackendRole.MailStore].Settings.Bind<ImapOptions>();
		Assert.Null(cleared.Security);
		Assert.Equal("imap.global", cleared.Host); // untouched global key still inherited
	}

	[Fact]
	public void MailAddress_IsExplicitFlag_AndNeverChangesMailUserName()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["phone"] = new() { MailAddress = "real@example.com" }
		};

		ResolvedUser account = Resolver(options, BaseConfig()).Resolve(new BackendCredentials("phone", "P"));
		Assert.Equal("real@example.com", account.MailAddress);
		Assert.True(account.MailAddressIsExplicit);
		// login, NOT the mail address
		Assert.Equal("phone", account.Roles[BackendRole.MailStore].Credentials.UserName);
	}

	[Fact]
	public void SealedMailStorePassword_IsUnsealedForTheBackend_ButNeverAuthenticatesTheDevice()
	{
		byte[] key = new byte[32];
		Array.Fill(key, (byte)9);
		ActiveSyncOptions options = HostOptions();
		options.Encryption = new EncryptionOptions { Key = Convert.ToBase64String(key) };
		options.Users = new Dictionary<string, UserOptions>
		{
			// The gateway password is REQUIRED alongside a stored MailStore secret (the probe
			// invariant); without it this entry would not validate at all.
			["u"] = new()
			{
				Password = "phone-pw",
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = SecretValue.Seal("real-mail-pw", key) } }
			}
		};
		UserResolver resolver = Resolver(options, BaseConfig());

		Assert.Equal("real-mail-pw", resolver.Resolve(new BackendCredentials("u", "ignored"))
			.Roles[BackendRole.MailStore].Credentials.Password);
		Assert.True(resolver.VerifyLocally("u", "phone-pw"));
		// The unsealed BACKEND secret is not a device credential, sealed or not.
		Assert.False(resolver.VerifyLocally("u", "real-mail-pw"));
	}

	// ---------- local auth rules ----------

	[Fact]
	public void VerifyLocally_PrecedenceMatrix()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["hashed"] = new() { Password = GatewayPasswordHasher.Hash("gw-secret") },
			["plain"] = new() { Password = "gw-plain" },
			// A stored MailStore password REQUIRES this gateway password (the probe invariant), and
			// only the gateway password ever authenticates a device.
			["both"] = new()
			{
				Password = "gateway-wins",
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = "mail-pw" } }
			},
			["probe-me"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { UserName = "other" } }
			}
		};
		UserResolver resolver = Resolver(options, BaseConfig());

		Assert.True(resolver.VerifyLocally("hashed", "gw-secret"));
		Assert.False(resolver.VerifyLocally("hashed", "wrong"));
		Assert.True(resolver.VerifyLocally("plain", "gw-plain"));
		Assert.True(resolver.VerifyLocally("both", "gateway-wins"));
		Assert.False(resolver.VerifyLocally("both", "mail-pw")); // backend pw is not the phone pw
		Assert.Null(resolver.VerifyLocally("probe-me", "anything")); // no local rule → probe
		Assert.Null(resolver.VerifyLocally("undeclared", "anything"));
		// Case-insensitive lookup.
		Assert.True(resolver.VerifyLocally("PLAIN", "gw-plain"));
	}

	[Fact]
	public void AutoProvisionOff_RejectsUndeclared_WithoutProbing()
	{
		// AutoProvisionUsers=false absorbed the deleted RequireDeclaredUsers allowlist
		// (db-restructure decisions 6/7): undeclared logins are refused BEFORE any probe.
		ActiveSyncOptions options = HostOptions();
		options.AutoProvisionUsers = false;
		options.Users = new Dictionary<string, UserOptions> { ["allowed"] = new() };
		UserResolver resolver = Resolver(options, BaseConfig());

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
		options.Users = new Dictionary<string, UserOptions>
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
		UserResolver.ValidateUsers(options, Roles(config), Registry(), null, failures);
		string joined = string.Join(";", failures);
		Assert.Contains("control characters", joined);
		Assert.Contains("Host is required", joined);
		Assert.Contains("BaseUrl 'not-a-url'", joined);
	}

	[Fact]
	public void ValidateUsers_RejectsALoginWithLeadingOrTrailingWhitespace()
	{
		// B13: Basic auth delivers a login's leading/trailing spaces verbatim. ValidateLogin only
		// rejected ':' and control characters, so " bob" passed validation — it then misses
		// MergedUsers ( " bob" != "bob" ), degrades to pass-through, and (with AutoProvisionUsers)
		// mints a second, permanent identity the real "bob" can never see. The login must be
		// refused when it differs from its trimmed form, so the phantom identity is never minted.
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions> { [" bob"] = new() };

		List<string> failures = new();
		UserResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), null, failures);

		Assert.Contains(failures, f => f.Contains(" bob") && f.Contains("whitespace"));
	}

	[Fact]
	public void ValidateUsers_ValidationMemo_ReplaysSharedFailurePerUser()
	{
		// B7 (item 37): validation is memoized per (provider, role, settings-identity). Users that
		// inherit the same broken global MailStore section share the settings object, so validation
		// runs once — but the cached failure must still be reported for EVERY user (not just the first).
		Dictionary<string, string?> config = BaseConfig();
		config.Remove("ActiveSync:Backends:MailStore:Host"); // global MailStore now invalid for all
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["alice@x"] = new(),
			["bob@x"] = new(),
			["carol@x"] = new(),
		};

		List<string> failures = new();
		UserResolver.ValidateUsers(options, Roles(config), Registry(), null, failures);

		// One "Host is required" per user proves the memo replays the shared verdict rather than
		// swallowing it after the first cache hit.
		Assert.Equal(3, failures.Count(f => f.Contains("Host is required")));
	}

	[Fact]
	public void ValidateUsers_UnknownRole_AndUnknownProvider_AreReported()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
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
		UserResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), null, failures);
		string joined = string.Join(";", failures);
		Assert.Contains("Frisbee", joined);
		Assert.Contains("jmap", joined);
	}

	[Fact]
	public void ProviderOverride_IsTrimmed_BeforeLookupAndInheritance()
	{
		// B17: the global Provider is trimmed but the per-user override was used raw. " imap"
		// failed the equality check → inheritGlobal false → the role dropped the inherited
		// host/port/TLS, and registry.GetFor(" imap") threw an unrelated "unknown provider".
		ActiveSyncOptions options = HostOptions();
		UserOptions entry = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
				{ ["MailStore"] = new() { Provider = " imap" } },
		};

		List<string> failures = UserResolver.ValidateEntry(options, Roles(BaseConfig()), Registry(), "u", entry);
		Assert.Empty(failures); // trimmed → known provider AND inherits the global host

		options.Users = new Dictionary<string, UserOptions> { ["u"] = entry };
		ResolvedRole mailStore = Resolver(options, BaseConfig())
			.Resolve(new BackendCredentials("u", "P")).Roles[BackendRole.MailStore];
		Assert.Equal("imap", mailStore.ProviderName);
		Assert.Equal("imap.global", mailStore.Settings.Bind<ImapOptions>().Host); // inherited, not dropped
	}

	[Fact]
	public void UnconfiguredMailRole_WithUserOverride_ReportsTargetedFailure_NotProviderMisdiagnosis()
	{
		// B21: on an unconfigured gateway (no global MailStore) a user MailStore override took
		// providerName ?? "local" → registry.GetFor("local", MailStore) threw, crashing the
		// resolver ctor for a CONFIG user (host won't start) and misdiagnosing as "provider
		// 'local' does not support MailStore". Mirror the Oof handling with a clear message.
		ActiveSyncOptions options = HostOptions();
		UserOptions entry = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new() { Settings = new Dictionary<string, string?> { ["Host"] = "h" } },
			},
		};

		List<string> failures = UserResolver.ValidateEntry(
			options, Roles(new Dictionary<string, string?>()), Registry(), "u", entry);
		string joined = string.Join(";", failures);
		Assert.Contains("no global MailStore role is configured", joined);
		Assert.DoesNotContain("does not support the MailStore role", joined);
	}

	// ---------- the probe invariant, at the write paths ----------

	[Theory]
	[InlineData("DefaultBackendPassword")]
	[InlineData("MailStore")]
	public void ValidateEntry_AStoredMailSecret_WithoutAGatewayPassword_IsRefused(string where)
	{
		// Setting the backend secret first is the refused half of the rule. Without it the account
		// could only be decided by a probe that signs in with the gateway's own stored password,
		// which succeeds whatever the device sends.
		ActiveSyncOptions options = HostOptions();
		UserOptions entry = Storing(where);

		List<string> failures = UserResolver.ValidateEntry(options, Roles(BaseConfig()), Registry(), "u", entry);

		string joined = string.Join(";", failures);
		Assert.Contains("no gateway Password", joined);
		Assert.Contains("eas user password u", joined);
	}

	[Theory]
	[InlineData("DefaultBackendPassword")]
	[InlineData("MailStore")]
	public void ValidateEntry_AStoredMailSecret_IsAccepted_AlongsideAGatewayPassword(string where)
	{
		ActiveSyncOptions options = HostOptions();
		UserOptions entry = Storing(where);
		entry.Password = "phone-pw";

		Assert.Empty(UserResolver.ValidateEntry(options, Roles(BaseConfig()), Registry(), "u", entry));
	}

	[Fact]
	public void ValidateEntry_TheGatewayPasswordMayCome_FromTheOtherLevel()
	{
		// The two halves of the rule can live at DIFFERENT levels, which is why the row is judged as
		// merged over config rather than alone. Judged alone this write looks like a bare backend
		// secret and would be refused, though what takes effect is perfectly safe.
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions> { ["u"] = new() { Password = "phone-pw" } };

		Assert.Empty(UserResolver.ValidateEntry(
			options, Roles(BaseConfig()), Registry(), "u",
			new UserOptions { DefaultBackendPassword = "backend-secret" }));
	}

	[Fact]
	public void ValidateEntry_ABareRowCannotComplete_ARefusedCombinationHalfDeclaredInConfig()
	{
		// The inverse, and the reason judging the row alone would not just misfire but MISS: config
		// already supplies the backend secret, so a row that merely adds an unrelated field would
		// slip through while the merged account is exactly the refused shape. (Such a config entry
		// cannot start the gateway either — this is the second line.)
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["u"] = new() { DefaultBackendPassword = "backend-secret" }
		};

		List<string> failures = UserResolver.ValidateEntry(
			options, Roles(BaseConfig()), Registry(), "u", new UserOptions { MailAddress = "u@example.com" });

		Assert.Contains("no gateway Password", string.Join(";", failures));
	}

	[Fact]
	public void ValidateEntry_RemovingTheGatewayPassword_IsRefusedWhileASecretRemains()
	{
		// The other half of one rule. A database null does not CLEAR a scalar — it falls through to
		// config (UserMerge) — so "remove the gateway password" is only really a removal when config
		// has none either, and only the merge can tell.
		ActiveSyncOptions options = HostOptions();
		UserOptions entry = new() { Password = null, DefaultBackendPassword = "backend-secret" };

		Assert.Contains("no gateway Password", string.Join(";",
			UserResolver.ValidateEntry(options, Roles(BaseConfig()), Registry(), "u", entry)));

		options.Users = new Dictionary<string, UserOptions> { ["u"] = new() { Password = "config-pw" } };
		Assert.Empty(UserResolver.ValidateEntry(options, Roles(BaseConfig()), Registry(), "u", entry));
	}

	[Fact]
	public void ValidateEntry_AContentRoleSecret_NeedsNoGatewayPassword()
	{
		// MailStore is the probe target, so it is the only role the rule concerns.
		ActiveSyncOptions options = HostOptions();
		UserOptions entry = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
				{ ["Calendar"] = new() { Password = "dav-secret" } }
		};

		Assert.Empty(UserResolver.ValidateEntry(options, Roles(BaseConfig()), Registry(), "u", entry));
	}

	private static UserOptions Storing(string where) => where == "DefaultBackendPassword"
		? new UserOptions { DefaultBackendPassword = "backend-secret" }
		: new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
				{ ["MailStore"] = new() { Password = "backend-secret" } }
		};

	[Fact]
	public void ValidateUsers_MalformedGatewayPasswordHash_IsReported()
	{
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions> { ["u"] = new() { Password = "pbkdf2$broken" } };

		List<string> failures = new();
		UserResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), null, failures);
		Assert.Contains("not a valid pbkdf2$ value", string.Join(";", failures));
	}

	[Fact]
	public void ValidateUsers_SealedValueWithoutKey_IsReported()
	{
		byte[] key = new byte[32];
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["u"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
					{ ["MailStore"] = new() { Password = SecretValue.Seal("pw", key) } }
			}
		};

		List<string> failures = new();
		UserResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), null, failures);
		Assert.Contains("sealed (enc:v1:) but no ActiveSync:Encryption key", string.Join(";", failures));
	}

	[Fact]
	public void OnRolesChanged_ConfigUserMergeFailsAfterLiveEdit_KeepsPreviousSnapshot_DoesNotThrow()
	{
		// B6: a live backend edit that invalidates a CONFIG user's merge made OnRolesChanged's
		// BuildSnapshot throw (config users are treated as strict — startup already validated them,
		// which is untrue after a live edit). The throw escaped through the roles-Changed invocation
		// and out of the settings reload, mislogged as a settings failure, leaving the snapshot stale
		// forever. It must instead be caught, keeping the previous (last-good) snapshot.
		DbSettingsConfigurationSource dbSource = new();
		// Oof lives entirely in the DB layer so clearing it removes the WHOLE section (role absent =
		// a valid rebuild that fires Changed). Left in the file layer, its Host key would keep the
		// section alive and an empty Provider would be a rejected rebuild that never fires.
		dbSource.Provider.SetData(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:Oof:Provider"] = "sieve",
			["ActiveSync:Backends:Oof:Host"] = "sieve.global",
		});
		IConfigurationRoot root = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "imap",
				["ActiveSync:Backends:MailStore:Host"] = "imap.global",
				["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
				["ActiveSync:Backends:MailSubmit:Host"] = "smtp.global",
			})
			.Add(dbSource)
			.Build();
		BackendRolesProvider rolesProvider = new(root, Registry());
		ActiveSyncOptions options = HostOptions();
		// The config user has an Oof override that inherits the global sieve provider — valid now.
		options.Users = new Dictionary<string, UserOptions>
		{
			["u"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride> { ["Oof"] = new() },
			},
		};
		UserResolver resolver = new(TestOptionsMonitor.Of(options), rolesProvider, Registry());
		Assert.Contains("u", resolver.MergedUsers.Keys);

		// Remove the global Oof role live (role-valid — Oof is optional). The config user's Oof
		// override can no longer inherit a provider, so its merge now fails.
		Exception? ex = Record.Exception(() =>
			dbSource.Provider.SetData(new Dictionary<string, string?>()));

		Assert.Null(ex);                              // the throw must not escape the reload
		Assert.Contains("u", resolver.MergedUsers.Keys); // previous snapshot kept
	}

	[Fact]
	public void LiveUsersFileEdit_TakesEffectImmediately_NotAtAnUnrelatedLaterMoment()
	{
		// B17: ActiveSyncOptions.UsersFile is loaded with reloadOnChange:false (a restart is required
		// to pick up its own edits), but appsettings.json — where ActiveSync:Users can equally be
		// declared — reloads live by default. That live edit used to mutate
		// _options.CurrentValue.Users immediately while UserResolver kept resolving against the OLD
		// compiled snapshot until some UNRELATED trigger (a "users" DB-stamp poll or a Backends edit)
		// happened to rebuild it — landing on an arbitrary later request instead of "restart to
		// change" (the documented contract) or "takes effect live" (what actually happened to the
		// bound option). FIX: subscribe to IOptionsMonitor.OnChange and rebuild as soon as the
		// ActiveSync:Users subtree itself moves (mirroring BackendRolesProvider's Signature idiom), so
		// the edit takes effect on ITS OWN, with nothing else required.
		Dictionary<string, string?> config = BaseConfig();
		IConfigurationRoot root = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
		FiringOptionsMonitor<ActiveSyncOptions> monitor = new(HostOptions());
		UserResolver resolver = new(monitor, new BackendRolesProvider(root), Registry(), config: root);

		Assert.DoesNotContain("phone", resolver.MergedUsers.Keys);

		// The live file edit: a new ActiveSync:Users:phone entry appears in the reloadable config...
		root["ActiveSync:Users:phone:Password"] = "phone-secret";
		// ...and the real IOptionsMonitor<ActiveSyncOptions> (simulated here) recomputes and fires.
		ActiveSyncOptions updated = HostOptions();
		updated.Users = new Dictionary<string, UserOptions> { ["phone"] = new() { Password = "phone-secret" } };
		monitor.Set(updated);

		// No EnsureFreshAsync, no Backends edit, no other trigger — the Users edit must be visible now.
		Assert.Contains("phone", resolver.MergedUsers.Keys);
	}

	/// <summary>
	///   Unlike <see cref="TestOptionsMonitor" />'s fixed monitor, this one actually invokes its
	///   registered listeners on <see cref="Set" /> — needed to exercise a consumer's OnChange
	///   subscription (B17).
	/// </summary>
	private sealed class FiringOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
	{
		private T _value = initial;
		private readonly List<Action<T, string?>> _listeners = new();

		public T CurrentValue => _value;
		public T Get(string? name) => _value;

		public IDisposable? OnChange(Action<T, string?> listener)
		{
			_listeners.Add(listener);
			return null;
		}

		public void Set(T value)
		{
			_value = value;
			foreach (Action<T, string?> listener in _listeners.ToArray())
				listener(value, null);
		}
	}

	[Fact]
	public void ValidateUsers_SealedGatewayPassword_IsReported()
	{
		// B18: an enc:v1: value in the gateway Password position (NOT a backend role) is never a
		// valid credential. VerifyLocally treats a non-pbkdf2$ stored value as plaintext and
		// compares SHA256(sealed) against SHA256(presented), which never matches — so the real
		// password never authenticates and the account is silently locked out, with no report. It
		// must be flagged at validation time, mirroring the CLI/web write-path policy that already
		// rejects a sealed gateway password. Reported regardless of whether a key is present.
		byte[] key = new byte[32];
		ActiveSyncOptions options = HostOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["u"] = new() { Password = SecretValue.Seal("pw", key) }
		};

		List<string> failures = new();
		UserResolver.ValidateUsers(options, Roles(BaseConfig()), Registry(), key, failures);
		Assert.Contains("gateway Password", string.Join(";", failures));
	}
}
