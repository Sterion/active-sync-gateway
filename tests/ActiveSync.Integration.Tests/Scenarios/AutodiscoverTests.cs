using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;

namespace ActiveSync.Integration.Tests.Scenarios;

[Collection("gateway")]
[Trait("Category", "Integration")]
public class AutodiscoverTests(GatewayFixture gateway)
{
	private const string RequestNs =
		"http://schemas.microsoft.com/exchange/autodiscover/mobilesync/requestschema/2006";

	private static HttpRequestMessage BuildRequest(string user, string? password)
	{
		XDocument body = new(
			new XElement(XName.Get("Autodiscover", RequestNs),
				new XElement(XName.Get("Request", RequestNs),
					new XElement(XName.Get("EMailAddress", RequestNs), user),
					new XElement(XName.Get("AcceptableResponseSchema", RequestNs),
						"http://schemas.microsoft.com/exchange/autodiscover/mobilesync/responseschema/2006"))));
		HttpRequestMessage request = new(HttpMethod.Post, "/autodiscover/autodiscover.xml")
		{
			Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml")
		};
		if (password is not null)
			request.Headers.Authorization = new AuthenticationHeaderValue(
				"Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
		return request;
	}

	[BackendFact]
	public async Task Autodiscover_ReturnsMobileSyncEasUrl()
	{
		using HttpClient http = gateway.CreateHttpClient();
		using HttpResponseMessage response = await http.SendAsync(BuildRequest(TestBackend.User1, TestBackend.Password));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string xml = await response.Content.ReadAsStringAsync();
		XDocument doc = XDocument.Parse(xml);

		XNamespace resp = "http://schemas.microsoft.com/exchange/autodiscover/mobilesync/responseschema/2006";
		List<XElement> settings = doc.Descendants(resp + "Server").ToList();
		Assert.NotEmpty(settings);
		Assert.Contains(settings, s => s.Element(resp + "Type")?.Value == "MobileSync");
		string? url = settings.First().Element(resp + "Url")?.Value;
		Assert.EndsWith("/Microsoft-Server-ActiveSync", url);

		string? email = doc.Descendants(resp + "EMailAddress").FirstOrDefault()?.Value;
		Assert.Equal(TestBackend.User1, email);
	}

	[BackendFact]
	public async Task Autodiscover_RequiresAuthentication()
	{
		using HttpClient http = gateway.CreateHttpClient();
		using HttpResponseMessage response = await http.SendAsync(BuildRequest(TestBackend.User1, null));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[BackendEnforcesAuthFact]
	public async Task Autodiscover_RejectsBadCredentials()
	{
		using HttpClient http = gateway.CreateHttpClient();
		using HttpResponseMessage response = await http.SendAsync(BuildRequest(TestBackend.User1, "wrong-password"));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}
}
