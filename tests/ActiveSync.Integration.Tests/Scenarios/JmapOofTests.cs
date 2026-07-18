using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Core.Backend;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Out-of-office over JMAP (RFC 8621 VacationResponse): Settings→Oof Set arms the singleton
///   on the JMAP server, Get reflects the stored state, and disable turns it off. Verified
///   from both the EAS surface and a direct JMAP VacationResponse/get.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class JmapOofTests(GatewayFixture gateway)
{
	private static readonly XNamespace ST = EasNamespaces.Settings;

	[BackendFact]
	public async Task Oof_EnableGetDisable_RoundTrips_AndArmsTheVacationResponse()
	{
		EasTestClient client = gateway.CreateJmapEasClient(TestBackend.User2);

		XDocument? enabled = await client.PostAsync("Settings",
			OofSet(1, "I am out of the office over JMAP.\nBack Monday."));
		Assert.Equal("1", OofStatus(enabled));

		// The JMAP backend really has the vacation response armed.
		JsonElement? armed = await VacationResponseAsync(TestBackend.User2);
		Assert.NotNull(armed);
		Assert.True(armed.Value.GetProperty("isEnabled").GetBoolean());
		string body = armed.Value.TryGetProperty("textBody", out JsonElement t) ? t.GetString() ?? "" : "";
		Assert.Contains("out of the office", body);

		// EAS Get reflects the stored state.
		XElement? get = (await client.PostAsync("Settings", OofGet()))?.Root?
			.Element(ST + "Oof")?.Element(ST + "Get");
		Assert.Equal("1", get?.Element(ST + "OofState")?.Value);

		// Disable: the vacation response is turned off.
		Assert.Equal("1", OofStatus(await client.PostAsync("Settings", OofSet(0, null))));
		JsonElement? off = await VacationResponseAsync(TestBackend.User2);
		Assert.False(off?.GetProperty("isEnabled").GetBoolean());

		XElement? getOff = (await client.PostAsync("Settings", OofGet()))?.Root?
			.Element(ST + "Oof")?.Element(ST + "Get");
		Assert.Equal("0", getOff?.Element(ST + "OofState")?.Value);
	}

	private static async Task<JsonElement?> VacationResponseAsync(string user)
	{
		using JmapClient client = new(
			new Uri(TestBackend.JmapUrl!), new BackendCredentials(user, TestBackend.Password),
			allowInvalidCertificates: true);
		JmapSessionResource session = await client.GetSessionAsync(CancellationToken.None);
		using JmapResponse response = await client.CallAsync(
			[JmapCapabilities.Core, JmapCapabilities.VacationResponse], "VacationResponse/get",
			new Dictionary<string, object?>
			{
				["accountId"] = session.PrimaryAccount(JmapCapabilities.VacationResponse),
				["ids"] = null
			}, CancellationToken.None);
		JsonElement list = response.Arguments("0").GetProperty("list");
		return list.GetArrayLength() > 0 ? list[0].Clone() : null;
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
			set.Add(new XElement(ST + "OofMessage",
				new XElement(ST + "AppliesToInternal"),
				new XElement(ST + "Enabled", "1"),
				new XElement(ST + "ReplyMessage", message),
				new XElement(ST + "BodyType", "Text")));
		return new XDocument(new XElement(ST + "Settings", new XElement(ST + "Oof", set)));
	}

	private static string? OofStatus(XDocument? response)
	{
		return response?.Root?.Element(ST + "Oof")?.Element(ST + "Status")?.Value;
	}
}
