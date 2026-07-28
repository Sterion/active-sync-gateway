using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Sieve;
using ActiveSync.Backends.Smtp;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   `eas config unset`/the web settings DELETE deleted the database row with no validation at
///   all, unlike the write direction (<see cref="SettingKeys.ValidateStartupImpact" />). A removal can
///   just as easily persist a configuration the NEXT start refuses to boot on: a catalogue key whose
///   POCO default violates a cross-field rule now in force (here: a TLS certificate path removed while
///   its paired key path row remains), or a backend section a live gateway tolerates but
///   <c>BackendConfigurationValidator</c> throws on at the next restart (here: MailStore:Host).
/// </summary>
public sealed class ConfigRemovalValidationTests : IDisposable
{
	private readonly List<string> _tempFiles = [];

	public void Dispose()
	{
		foreach (string path in _tempFiles)
			File.Delete(path);
	}

	private string TempFile()
	{
		string path = Path.GetTempFileName();
		_tempFiles.Add(path);
		return path;
	}

	private static IConfiguration FileConfig(params (string Key, string? Value)[] pairs)
	{
		Dictionary<string, string?> data = new(StringComparer.OrdinalIgnoreCase)
		{
			["ActiveSync:Encryption:Key"] = Convert.ToBase64String(new byte[32]),
			["ActiveSync:Database:ConnectionString"] = "Data Source=:memory:",
		};
		foreach ((string key, string? value) in pairs)
			data[key] = value;
		return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
	}

	private static BackendProviderRegistry Registry() =>
		new(
		[
			new ImapBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new SmtpBackendProvider(NullLoggerFactory.Instance),
			new SieveBackendProvider(NullLoggerFactory.Instance),
		], NullLogger<BackendProviderRegistry>.Instance);

	// Config-removal example: `eas config unset ActiveSync:Backends:MailStore:Host` leaves a section the
	// running gateway survives (BackendRolesProvider keeps the last-good config), but
	// BackendConfigurationValidator.Validate throws "Host is required" at the very next start.
	[Fact]
	public void UnsettingBackendHost_WithNoFileFallback_IsRejected()
	{
		Dictionary<string, string?> db = new(StringComparer.OrdinalIgnoreCase)
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
		};

		string? error = SettingKeys.ValidateRemovalImpact(
			FileConfig(), db, Registry(), "ActiveSync:Backends:MailStore:Host");

		Assert.NotNull(error);
		Assert.Contains("Host is required", error);
	}

	// The same removal is harmless when the file/env layer underneath still supplies a Host — the
	// removal must fall through to the lower layer exactly like a real GlobalSettingStore.DeleteAsync,
	// not be treated as "gone entirely".
	[Fact]
	public void UnsettingBackendHost_WithAFileFallback_IsAccepted()
	{
		Dictionary<string, string?> db = new(StringComparer.OrdinalIgnoreCase)
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "db-override.example",
		};
		IConfiguration fileConfig = FileConfig(("ActiveSync:Backends:MailStore:Host", "file-value.example"));

		Assert.Null(SettingKeys.ValidateRemovalImpact(
			fileConfig, db, Registry(), "ActiveSync:Backends:MailStore:Host"));
	}

	// A catalogue-key cross-field brick: CertificatePath/CertificateKeyPath are a pair
	// (ValidateTls refuses a key path with no certificate path). Removing the certificate path while
	// its paired key path row remains reverts CertificatePath to its (unset) POCO default and
	// introduces exactly the failure ValidateStartupImpact exists to catch on the write side.
	[Fact]
	public void UnsettingHalfOfATlsCertificatePair_IsRejected()
	{
		string certPath = TempFile();
		string keyPath = TempFile();
		Dictionary<string, string?> db = new(StringComparer.OrdinalIgnoreCase)
		{
			["ActiveSync:Tls:CertificatePath"] = certPath,
			["ActiveSync:Tls:CertificateKeyPath"] = keyPath,
		};

		// Sanity: the pair together is valid before the removal.
		Assert.Null(SettingKeys.ValidateRemovalImpact(FileConfig(), db, Registry(), "ActiveSync:ReadOnly"));

		string? error = SettingKeys.ValidateRemovalImpact(
			FileConfig(), db, Registry(), "ActiveSync:Tls:CertificatePath");
		Assert.NotNull(error);
		Assert.Contains("CertificateKeyPath is set without", error);
	}

	// The section-removal path already checks the pending removal against BackendRolesConfig.Load and
	// each assigned provider's own ValidateConfiguration, and the WRITE path (BackendKeyValidator.Validate)
	// already checks a pending write against UserResolver.ValidateUsers -- but the removal path never ran
	// the declared-user check at all. Removing the only global Oof assignment while a config user's own
	// Oof override still names no explicit Provider is exactly the scenario UserResolver flags with "no
	// global Oof role is configured" at the next boot; the removal must surface that failure now, the
	// same way it already surfaces a backend-section shape failure.
	[Fact]
	public void UnsettingTheOnlyBackendRoleAssignment_WithADeclaredUserOverride_IsRejected()
	{
		Dictionary<string, string?> db = new(StringComparer.OrdinalIgnoreCase)
		{
			["ActiveSync:Backends:Oof:Provider"] = "sieve",
			["ActiveSync:Backends:Oof:Host"] = "sieve.example",
			["ActiveSync:Users:bob:Backends:Oof:UserName"] = "bob-oof",
		};

		// Sanity: the pair together is valid before the removal.
		Assert.Null(SettingKeys.ValidateRemovalImpact(FileConfig(), db, Registry(), "ActiveSync:ReadOnly"));

		string? error = SettingKeys.ValidateRemovalImpact(
			FileConfig(), db, Registry(), "ActiveSync:Backends:Oof:Provider");

		Assert.NotNull(error);
		Assert.Contains("no global Oof role is configured", error);
	}

	// A removal that changes nothing observable must not be flagged.
	[Fact]
	public void UnsettingAnUnrelatedKey_IsAccepted()
	{
		Dictionary<string, string?> db = new(StringComparer.OrdinalIgnoreCase)
		{
			["ActiveSync:ReadOnly"] = "true",
		};

		Assert.Null(SettingKeys.ValidateRemovalImpact(FileConfig(), db, Registry(), "ActiveSync:ReadOnly"));
	}
}
