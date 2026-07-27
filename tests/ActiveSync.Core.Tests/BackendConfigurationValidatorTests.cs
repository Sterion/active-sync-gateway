using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Local;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   B12: a global <c>ActiveSync:Backends:&lt;Role&gt;:Password</c>/<c>UserName</c> is refused by
///   `eas config set` / the web settings PUT (<see cref="Administration.BackendKeyValidator" />'s
///   <c>InertCredentialLeaf</c>), but nothing at startup looked at the SAME key — so an operator who
///   put it in a config file got no warning that it is silently read by nothing (no provider binds
///   Password/UserName from <see cref="ProviderSettings" />; credentials are resolved per user).
/// </summary>
public sealed class BackendConfigurationValidatorTests
{
	private static BackendProviderRegistry Registry() =>
		new(
			[
				new ImapBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
				new LocalBackendProvider(null!, null!, null!),
			],
			NullLogger<BackendProviderRegistry>.Instance);

	private static IConfiguration Config(Dictionary<string, string?> values) =>
		new ConfigurationBuilder().AddInMemoryCollection(values).Build();

	[Fact]
	public void AGlobalPasswordLeaf_FromAConfigFile_FailsStartup_EvenThoughItNeverWentThroughAWriteSurface()
	{
		IConfiguration config = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
			["ActiveSync:Backends:MailStore:Password"] = "hunter2",
		});
		ActiveSyncOptions options = config.GetSection("ActiveSync").Get<ActiveSyncOptions>() ?? new ActiveSyncOptions();
		BackendConfigurationValidator validator = new(Microsoft.Extensions.Options.Options.Create(options), config, Registry());

		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(validator.Validate);
		Assert.Contains("no provider reads it", ex.Message);
	}

	[Fact]
	public void ARoleWithNoInertCredentialLeaf_StartsCleanly()
	{
		IConfiguration config = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
		});
		ActiveSyncOptions options = config.GetSection("ActiveSync").Get<ActiveSyncOptions>() ?? new ActiveSyncOptions();
		BackendConfigurationValidator validator = new(Microsoft.Extensions.Options.Options.Create(options), config, Registry());

		validator.Validate(); // must not throw
	}
}
