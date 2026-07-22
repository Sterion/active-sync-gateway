using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Local;
using ActiveSync.Backends.Smtp;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The live backend-role configuration provider: it rebuilds only when the ActiveSync:Backends
///   subtree changes (driven here through the real database settings provider's reload token), and
///   raises Changed so the session cache can recycle exactly for backend edits.
/// </summary>
public sealed class BackendRolesProviderTests
{
	private static (BackendRolesProvider Provider, DbSettingsConfigurationProvider Db) Build()
	{
		DbSettingsConfigurationSource dbSource = new();
		IConfigurationRoot config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "imap",
				["ActiveSync:Backends:MailStore:Host"] = "imap.old",
				["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
				["ActiveSync:Backends:MailSubmit:Host"] = "smtp.old",
			})
			.Add(dbSource)
			.Build();
		return (new BackendRolesProvider(config), dbSource.Provider);
	}

	private static string? Host(BackendRolesProvider provider, BackendRole role) =>
		provider.Current.Assignments[role].Settings.Section["Host"];

	private static BackendProviderRegistry Registry() =>
		new(
		[
			new ImapBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new SmtpBackendProvider(NullLoggerFactory.Instance),
			// The content roles fall back to "local", so the rebuild validates it too — like real DI.
			// Only ValidateConfiguration is exercised, so the null connection deps are never touched.
			new LocalBackendProvider(null!, null!, null!),
		], NullLogger<BackendProviderRegistry>.Instance);

	private static (BackendRolesProvider Provider, DbSettingsConfigurationProvider Db) BuildWithRegistry()
	{
		DbSettingsConfigurationSource dbSource = new();
		IConfigurationRoot config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "imap",
				["ActiveSync:Backends:MailStore:Host"] = "imap.old",
				["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
				["ActiveSync:Backends:MailSubmit:Host"] = "smtp.old",
			})
			.Add(dbSource)
			.Build();
		return (new BackendRolesProvider(config, Registry()), dbSource.Provider);
	}

	[Fact]
	public void BackendChange_RebuildsCurrent_AndRaisesChanged()
	{
		(BackendRolesProvider provider, DbSettingsConfigurationProvider db) = Build();
		int changed = 0;
		provider.Changed += () => changed++;
		Assert.Equal("imap.old", Host(provider, BackendRole.MailStore));

		// A database override of a backend host wins and triggers a rebuild.
		db.SetData(new Dictionary<string, string?> { ["ActiveSync:Backends:MailStore:Host"] = "imap.new" });
		Assert.Equal(1, changed);
		Assert.Equal("imap.new", Host(provider, BackendRole.MailStore));
	}

	[Fact]
	public void NonBackendChange_DoesNotRebuild()
	{
		(BackendRolesProvider provider, DbSettingsConfigurationProvider db) = Build();
		int changed = 0;
		provider.Changed += () => changed++;

		// A settings change that leaves the Backends subtree untouched must not recycle sessions.
		db.SetData(new Dictionary<string, string?> { ["ActiveSync:ReadOnly"] = "true" });
		Assert.Equal(0, changed);
		Assert.Equal("imap.old", Host(provider, BackendRole.MailStore));
	}

	[Fact]
	public void NoMailBackends_StartsUnconfigured_ThenConfiguresLive()
	{
		// An empty backend configuration must NOT throw (start-without-config) and reports
		// unconfigured — the gateway starts so it can be set up via `eas config set`.
		DbSettingsConfigurationSource dbSource = new();
		IConfigurationRoot config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>())
			.Add(dbSource)
			.Build();
		BackendRolesProvider provider = new(config);
		Assert.False(provider.Current.IsMailConfigured);

		int changed = 0;
		provider.Changed += () => changed++;

		// Configuring both mail roles live (as `eas config set` would) flips it to configured.
		dbSource.Provider.SetData(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.x",
			["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
			["ActiveSync:Backends:MailSubmit:Host"] = "smtp.x",
		});
		Assert.Equal(1, changed);
		Assert.True(provider.Current.IsMailConfigured);
	}

	[Fact]
	public void InvalidBackendChange_IsIgnored_KeepsLastGood()
	{
		(BackendRolesProvider provider, DbSettingsConfigurationProvider db) = Build();
		int changed = 0;
		provider.Changed += () => changed++;

		// Dropping the mandatory MailStore provider is invalid — the live rebuild is lenient and
		// keeps the last-good configuration rather than taking the gateway down.
		db.SetData(new Dictionary<string, string?> { ["ActiveSync:Backends:MailStore:Provider"] = "" });
		Assert.Equal(0, changed);
		Assert.Equal("imap.old", Host(provider, BackendRole.MailStore));
	}

	// B14 — the live rebuild now runs each provider's ValidateConfiguration, not just shape parsing.
	// Blanking a Host is shape-valid but semantically invalid (imap RequiredHost), so it must be
	// rejected and the last-good configuration kept — the same delayed-brick class B1 fixes.
	[Fact]
	public void LiveEdit_ThatFailsProviderValidation_IsRejected_KeepsLastGood()
	{
		(BackendRolesProvider provider, DbSettingsConfigurationProvider db) = BuildWithRegistry();
		int changed = 0;
		provider.Changed += () => changed++;

		db.SetData(new Dictionary<string, string?> { ["ActiveSync:Backends:MailStore:Host"] = "" });
		Assert.Equal(0, changed);
		Assert.Equal("imap.old", Host(provider, BackendRole.MailStore));
	}

	// A live edit the provider accepts still applies (validation is a gate, not a wall).
	[Fact]
	public void LiveEdit_ThatPassesProviderValidation_IsApplied()
	{
		(BackendRolesProvider provider, DbSettingsConfigurationProvider db) = BuildWithRegistry();
		int changed = 0;
		provider.Changed += () => changed++;

		db.SetData(new Dictionary<string, string?> { ["ActiveSync:Backends:MailStore:Host"] = "imap.new" });
		Assert.Equal(1, changed);
		Assert.Equal("imap.new", Host(provider, BackendRole.MailStore));
	}

	// B14 (compounding) — a materialized snapshot means a REJECTED rebuild's settings never read
	// through: Host stays the last-good value even though the live section now holds the bad one.
	[Fact]
	public void RejectedRebuild_SettingsDoNotReadThrough_TheLiveSection()
	{
		(BackendRolesProvider provider, DbSettingsConfigurationProvider db) = BuildWithRegistry();

		// A change that fails validation (blank Host) — the assignment must keep the old snapshot,
		// not the live section which would now surface the empty value.
		db.SetData(new Dictionary<string, string?> { ["ActiveSync:Backends:MailStore:Host"] = "" });
		Assert.Equal("imap.old", Host(provider, BackendRole.MailStore));
	}
}
