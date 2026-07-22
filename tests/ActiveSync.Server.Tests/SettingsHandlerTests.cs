using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Security;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Settings (MS-ASCMD 2.2.1.18): ReadOnly mode must refuse arming a server-side out-of-office
///   auto-reply (F47), and an unrecognized section must get its own per-section status rather than
///   silent success (F48).
/// </summary>
public sealed class SettingsHandlerTests : IDisposable
{
	private static readonly XNamespace ST = EasNamespaces.Settings;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	private SettingsHandler NewHandler()
	{
		return new SettingsHandler(TestOptionsMonitor.SnapshotOf(_harness.Options),
			LocalContentProtector.CreatePlaintext(), NullLogger<SettingsHandler>.Instance);
	}

	// F47 — a read-only gateway must not install a real ManageSieve auto-reply rule (a far more
	// externally-visible side effect than the writes ReadOnly already blocks).
	[Fact]
	public async Task ReadOnly_OofSet_IsRefused_WithoutArmingBackend()
	{
		_harness.Options.ReadOnly = true;
		StubOof oof = new();
		_harness.Session.Oof = oof;

		XDocument? response = await _harness.RunAsync(NewHandler(), "Settings",
			new XDocument(new XElement(ST + "Settings",
				new XElement(ST + "Oof",
					new XElement(ST + "Set",
						new XElement(ST + "OofState", "1"),
						new XElement(ST + "OofMessage",
							new XElement(ST + "AppliesToInternal"),
							new XElement(ST + "ReplyMessage", "Away"),
							new XElement(ST + "BodyType", "Text")))))));

		XElement? oofResult = response?.Root?.Element(ST + "Oof");
		Assert.Equal("3", oofResult?.Element(ST + "Status")?.Value);
		Assert.False(oof.Enabled, "the Oof backend must not be armed in read-only mode");
	}

	// Control: with ReadOnly off the same request arms the backend and reports success.
	[Fact]
	public async Task Writable_OofSet_ArmsBackend()
	{
		StubOof oof = new();
		_harness.Session.Oof = oof;

		XDocument? response = await _harness.RunAsync(NewHandler(), "Settings",
			new XDocument(new XElement(ST + "Settings",
				new XElement(ST + "Oof",
					new XElement(ST + "Set",
						new XElement(ST + "OofState", "1"),
						new XElement(ST + "OofMessage",
							new XElement(ST + "AppliesToInternal"),
							new XElement(ST + "ReplyMessage", "Away"),
							new XElement(ST + "BodyType", "Text")))))));

		XElement? oofResult = response?.Root?.Element(ST + "Oof");
		Assert.Equal("1", oofResult?.Element(ST + "Status")?.Value);
		Assert.True(oof.Enabled);
	}

	// F48 — an unrecognized section must produce a per-section status 2 (not implemented), so the
	// client can tell it was not applied rather than getting a bare top-level Status 1 and silence.
	[Fact]
	public async Task UnknownSection_GetsPerSectionStatus()
	{
		XDocument? response = await _harness.RunAsync(NewHandler(), "Settings",
			new XDocument(new XElement(ST + "Settings",
				new XElement(ST + "RightsManagementInformation",
					new XElement(ST + "Get")))));

		XElement? section = response?.Root?.Element(ST + "RightsManagementInformation");
		Assert.NotNull(section);
		Assert.Equal("2", section.Element(ST + "Status")?.Value);
	}

	private sealed class StubOof : IOofBackend
	{
		public bool Enabled { get; private set; }

		public Task<string?> EnableAsync(OofReply reply, CancellationToken ct)
		{
			Enabled = true;
			return Task.FromResult<string?>("");
		}

		public Task DisableAsync(string restoreToken, CancellationToken ct)
		{
			Enabled = false;
			return Task.CompletedTask;
		}
	}
}
