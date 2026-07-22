using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Smtp;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   B24 — validating a single backend key in isolation. When the key is <c>Provider</c>, it is not
///   enough that the registry can serve the role: the settings ALREADY stored under that role must
///   also satisfy the incoming provider's schema, otherwise the switch is accepted over a section
///   shaped for the old provider and only surfaces at the next restart.
/// </summary>
public sealed class BackendKeyValidatorTests
{
	private static BackendProviderRegistry Registry() =>
		new(
		[
			new ImapBackendProvider(TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new SmtpBackendProvider(NullLoggerFactory.Instance),
		], NullLogger<BackendProviderRegistry>.Instance);

	private static IConfiguration Config(Dictionary<string, string?> values) =>
		new ConfigurationBuilder().AddInMemoryCollection(values).Build();

	// The bug: the section stored under the role does not satisfy the incoming provider (imap needs a
	// Host, and none is stored), yet the switch used to be accepted because imap CAN serve MailStore.
	[Fact]
	public void ProviderChange_OverSettingsThatFailTheNewSchema_IsRejected()
	{
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Port"] = "993", // no Host
		});

		string? error = BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Provider", "imap");
		Assert.NotNull(error);
	}

	[Fact]
	public void ProviderChange_OverSettingsThatSatisfyTheNewSchema_IsAccepted()
	{
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
		});

		Assert.Null(BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Provider", "imap"));
	}

	// Existing behaviour preserved: a provider that cannot serve the role is still rejected outright.
	[Fact]
	public void ProviderChange_ToAProviderThatCannotServeTheRole_IsRejected()
	{
		IConfiguration effective = Config(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = "imap.example",
		});

		Assert.NotNull(BackendKeyValidator.Validate(
			Registry(), effective, "ActiveSync:Backends:MailStore:Provider", "smtp"));
	}
}
