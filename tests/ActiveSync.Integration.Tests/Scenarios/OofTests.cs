using System.Net.Sockets;
using System.Xml.Linq;
using ActiveSync.Backends.Sieve;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Out-of-office via ManageSieve: Settings→Oof Set arms the gateway-owned vacation script
///   on the sieve server, Get reads back from the state DB, and disable restores whatever
///   script was active before. Verified from both sides — the EAS surface and a direct
///   ManageSieve connection to the backend.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class OofTests(GatewayFixture gateway)
{
	private static readonly XNamespace ST = EasNamespaces.Settings;

	[SieveBackendFact]
	public async Task Oof_EnableGetDisable_RoundTrips_AndArmsTheSieveScript()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User2);

		// Enable with a message that exercises quoting and multi-line bodies.
		XDocument? enabled = await client.PostAsync("Settings", OofSet(1,
			"I am out of the office.\nBack on Monday — \"urgent\" issues: call the desk."));
		Assert.Equal("1", OofStatus(enabled));

		// Get reflects the stored state, same message for all three audiences.
		XElement? get = (await client.PostAsync("Settings", OofGet()))?.Root?
			.Element(ST + "Oof")?.Element(ST + "Get");
		Assert.NotNull(get);
		Assert.Equal("1", get.Element(ST + "OofState")?.Value);
		List<XElement> messages = get.Elements(ST + "OofMessage").ToList();
		Assert.Equal(3, messages.Count);
		Assert.All(messages, m => Assert.Contains("out of the office",
			m.Element(ST + "ReplyMessage")?.Value ?? ""));

		// The backend really has our script, and it is the active one.
		IReadOnlyList<(string Name, bool Active)> armed = await ListScriptsAsync(TestBackend.User2);
		Assert.Contains(armed, s => s.Name == SieveVacationScript.ScriptName && s.Active);

		// Disable: script gone, nothing active (there was no previous user script).
		XDocument? disabled = await client.PostAsync("Settings", OofSet(0, null));
		Assert.Equal("1", OofStatus(disabled));
		IReadOnlyList<(string Name, bool Active)> after = await ListScriptsAsync(TestBackend.User2);
		Assert.DoesNotContain(after, s => s.Name == SieveVacationScript.ScriptName);
		Assert.DoesNotContain(after, s => s.Active);

		XElement? getOff = (await client.PostAsync("Settings", OofGet()))?.Root?
			.Element(ST + "Oof")?.Element(ST + "Get");
		Assert.Equal("0", getOff?.Element(ST + "OofState")?.Value);
	}

	[SieveBackendFact]
	public async Task Oof_Disable_RestoresThePreviouslyActiveUserScript()
	{
		const string personalScript = "personal-filter";
		await using (ManageSieveClient direct = Direct(TestBackend.User1))
		{
			await direct.ConnectAsync(CancellationToken.None);
			await direct.PutScriptAsync(personalScript, "# user's own filter\r\nkeep;\r\n",
				CancellationToken.None);
			await direct.SetActiveAsync(personalScript, CancellationToken.None);
		}

		try
		{
			EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
			Assert.Equal("1", OofStatus(await client.PostAsync("Settings", OofSet(1, "away"))));
			IReadOnlyList<(string Name, bool Active)> armed = await ListScriptsAsync(TestBackend.User1);
			Assert.Contains(armed, s => s.Name == SieveVacationScript.ScriptName && s.Active);
			Assert.Contains(armed, s => s is { Name: personalScript, Active: false });

			Assert.Equal("1", OofStatus(await client.PostAsync("Settings", OofSet(0, null))));
			IReadOnlyList<(string Name, bool Active)> restored = await ListScriptsAsync(TestBackend.User1);
			Assert.Contains(restored, s => s is { Name: personalScript, Active: true });
			Assert.DoesNotContain(restored, s => s.Name == SieveVacationScript.ScriptName);
		}
		finally
		{
			await using ManageSieveClient cleanup = Direct(TestBackend.User1);
			await cleanup.ConnectAsync(CancellationToken.None);
			await cleanup.SetActiveAsync("", CancellationToken.None);
			await cleanup.DeleteScriptAsync(personalScript, CancellationToken.None);
			await cleanup.DeleteScriptAsync(SieveVacationScript.ScriptName, CancellationToken.None);
		}
	}

	[SieveBackendFact]
	public async Task Oof_Scheduled_RequiresStartAndEnd()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User2);
		XDocument? response = await client.PostAsync("Settings", new XDocument(
			new XElement(ST + "Settings",
				new XElement(ST + "Oof",
					new XElement(ST + "Set",
						new XElement(ST + "OofState", "2"),
						OofMessage("scheduled away"))))));
		Assert.Equal("6", OofStatus(response)); // conflicting arguments: no window given
	}

	// ---------- helpers ----------

	private static ManageSieveClient Direct(string user)
	{
		return new ManageSieveClient(
			new SieveOptions
			{
				Host = TestBackend.SieveHost, Port = TestBackend.SievePort,
				AllowInvalidCertificates = true
			},
			new BackendCredentials(user, TestBackend.Password));
	}

	private static async Task<IReadOnlyList<(string Name, bool Active)>> ListScriptsAsync(string user)
	{
		await using ManageSieveClient client = Direct(user);
		await client.ConnectAsync(CancellationToken.None);
		return await client.ListScriptsAsync(CancellationToken.None);
	}

	private static XDocument OofGet()
	{
		return new XDocument(new XElement(ST + "Settings",
			new XElement(ST + "Oof",
				new XElement(ST + "Get", new XElement(ST + "BodyType", "Text")))));
	}

	private static XDocument OofSet(int state, string? message)
	{
		XElement set = new(ST + "Set", new XElement(ST + "OofState", state.ToString()));
		if (message is not null)
			set.Add(OofMessage(message));
		return new XDocument(new XElement(ST + "Settings", new XElement(ST + "Oof", set)));
	}

	private static XElement OofMessage(string message)
	{
		return new XElement(ST + "OofMessage",
			new XElement(ST + "AppliesToInternal"),
			new XElement(ST + "Enabled", "1"),
			new XElement(ST + "ReplyMessage", message),
			new XElement(ST + "BodyType", "Text"));
	}

	private static string? OofStatus(XDocument? response)
	{
		return response?.Root?.Element(ST + "Oof")?.Element(ST + "Status")?.Value;
	}
}

/// <summary>A [Fact] that runs only when both the mail backend AND a ManageSieve listener are reachable.</summary>
public sealed class SieveBackendFactAttribute : FactAttribute
{
	private static readonly Lazy<string?> Reason = new(() =>
	{
		if (!TestBackend.IsAvailable)
			return TestBackend.SkipReason;
		try
		{
			using TcpClient probe = new();
			IAsyncResult result = probe.BeginConnect(TestBackend.SieveHost, TestBackend.SievePort, null, null);
			if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)) || !probe.Connected)
				return $"No ManageSieve listener at {TestBackend.SieveHost}:{TestBackend.SievePort} " +
				       "(the mailserver stack has none; Stalwart needs the sieve listener).";
			probe.EndConnect(result);
			return null;
		}
		catch (Exception ex)
		{
			return $"No ManageSieve listener at {TestBackend.SieveHost}:{TestBackend.SievePort} " +
			       $"({ex.GetBaseException().Message}).";
		}
	});

	public SieveBackendFactAttribute()
	{
		if (Reason.Value is not null)
			Skip = Reason.Value;
	}
}
