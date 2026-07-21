using ActiveSync.Core.Administration;
using ActiveSync.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Host-controlled settings (K38 / B22): keys that name something the host executes or reads
///   from the filesystem at startup must come from the environment or a config file only. A
///   database row for one of them turns settings-write access (admin UI, `eas config set`, a
///   DBA, SQL injection anywhere) into "point the gateway at another directory of assemblies" —
///   in-process arbitrary code execution with the master key in memory.
/// </summary>
public sealed class HostControlledSettingsTests
{
	[Theory]
	[InlineData("ActiveSync:Plugins:Directory")]
	[InlineData("ActiveSync:UsersFile")]
	[InlineData("ActiveSync:Database:ConnectionString")]
	[InlineData("ActiveSync:Encryption:KeyFile")]
	public void HostControlledKeys_AreNotSettable(string key)
	{
		// Neither enumerated as editable nor resolvable, so every write surface refuses them.
		Assert.DoesNotContain(SettingKeys.All, k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
		Assert.Null(SettingKeys.Find(key));
	}

	[Theory]
	[InlineData("ActiveSync:ReadOnly")]
	[InlineData("ActiveSync:Eas:MaxWindowSize")]
	[InlineData("ActiveSync:Backends:MailStore:Host")]
	public void OrdinarySettings_StayWritable(string key)
	{
		Assert.NotNull(SettingKeys.Find(key));
	}

	/// <summary>
	///   The write path is only half of it — a row written straight into the table (a DBA, or SQL
	///   injection through some other endpoint) never passes through it. The configuration
	///   provider drops host-controlled keys as it takes the snapshot, so a stored row is inert.
	/// </summary>
	[Fact]
	public void DatabaseRow_ForAHostControlledKey_IsIgnoredByTheConfigurationProvider()
	{
		DbSettingsConfigurationSource source = new();
		IConfigurationRoot config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ActiveSync:Plugins:Directory"] = "/app/plugins"
			})
			.Add(source)
			.Build();

		source.Provider.SetData(new Dictionary<string, string?>
		{
			["ActiveSync:Plugins:Directory"] = "/tmp/attacker",
			["ActiveSync:UsersFile"] = "/etc/shadow",
			["ActiveSync:ReadOnly"] = "true"
		});

		// The file/env value survives; the database row does not exist as far as the host is concerned.
		Assert.Equal("/app/plugins", config["ActiveSync:Plugins:Directory"]);
		Assert.Null(config["ActiveSync:UsersFile"]);
		// An ordinary setting in the same snapshot still applies — the filter is targeted.
		Assert.Equal("true", config["ActiveSync:ReadOnly"]);
	}
}
