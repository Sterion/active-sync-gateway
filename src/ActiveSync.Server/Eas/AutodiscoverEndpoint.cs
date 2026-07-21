using System.Text;
using System.Xml;
using System.Xml.Linq;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Logging;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Autodiscover for Exchange ActiveSync (MS-ASCMD / the Outlook "MobileSync" response schema).
///   A client POSTs its email address; we return the URL of our EAS endpoint so phones can be
///   added with just an address + password instead of a manually typed server name.
/// </summary>
public static class AutodiscoverEndpoint
{
	private static readonly XNamespace Request =
		"http://schemas.microsoft.com/exchange/autodiscover/mobilesync/requestschema/2006";

	private static readonly XNamespace Response =
		"http://schemas.microsoft.com/exchange/autodiscover/mobilesync/responseschema/2006";

	private static readonly XNamespace Base =
		"http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006";

	public static void Map(WebApplication app)
	{
		// ASP.NET route matching is case-insensitive, so these cover the ".xml" (Exchange)
		// and ".json" (modern Outlook) variants clients probe, in any casing.
		app.MapMethods("/autodiscover/autodiscover.xml", ["POST", "GET"], Handle);
		app.MapMethods("/autodiscover/autodiscover.json", ["POST", "GET"], Handle);
	}

	private static async Task Handle(
		HttpContext http,
		IBackendSessionFactory sessionFactory,
		AuthThrottle authThrottle,
		AccountResolver resolver,
		SyncStateService state,
		IOptionsSnapshot<ActiveSyncOptions> options,
		BackendRolesProvider rolesProvider,
		ILoggerFactory loggerFactory)
	{
		CancellationToken ct = http.RequestAborted;

		// Unconfigured gateway (no mail backend yet): answer 503 until configured via `eas config set`.
		if (!rolesProvider.Current.IsMailConfigured)
		{
			loggerFactory.CreateLogger("ActiveSync.Autodiscover")
				.LogWarning("Autodiscover request refused: the gateway has no mail backend configured (503)");
			http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			return;
		}

		// Autodiscover is authenticated (Basic) just like the EAS endpoint, and shares its
		// brute-force throttle and credential-verification prologue.
		string clientKey = EndpointAuth.ClientKey(http, options.Value.Auth);
		if (EndpointAuth.IsThrottled(http, authThrottle, clientKey))
			return;
		BackendCredentials? credentials = HttpBasicAuth.Parse(http.Request.Headers.Authorization.ToString());
		if (credentials is null)
		{
			HttpBasicAuth.Challenge(http);
			return;
		}

		ILogger logger = loggerFactory.CreateLogger("ActiveSync.Autodiscover");
		if (!await EndpointAuth.AuthenticateAsync(
			    http, sessionFactory, authThrottle, clientKey, credentials, logger, ct))
			return;

		// Autodiscover carries no device id, so only user-level operator blocks apply here.
		if (await state.IsLoginBlockedAsync(credentials.UserName, null, ct))
		{
			http.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		// An explicitly configured MailAddress is authoritative; otherwise keep the
		// pass-through chain (address the client posted, then the login).
		ILogger wireLogger = loggerFactory.CreateLogger(typeof(AutodiscoverEndpoint));
		ResolvedAccount account = resolver.Resolve(credentials);
		string? configuredAddress = account.MailAddressIsExplicit ? account.MailAddress : null;
		string email = configuredAddress ?? await ExtractEmailAsync(http, wireLogger, ct) ?? credentials.UserName;
		string easUrl = BuildEasUrl(http, options.Value.PublicUrl);

		XDocument doc = new(
			new XDeclaration("1.0", "utf-8", null),
			new XElement(Base + "Autodiscover",
				new XElement(Response + "Response",
					new XElement(Response + "Culture", "en:en"),
					new XElement(Response + "User",
						new XElement(Response + "DisplayName", email),
						new XElement(Response + "EMailAddress", email)),
					new XElement(Response + "Action",
						new XElement(Response + "Settings",
							new XElement(Response + "Server",
								new XElement(Response + "Type", "MobileSync"),
								new XElement(Response + "Url", easUrl),
								new XElement(Response + "Name", easUrl)))))));

		if (wireLogger.IsEnabled(LogLevel.Trace))
			wireLogger.LogTrace("Autodiscover {User} response: {Payload}",
				LogText.Clean(credentials.UserName, 128), WireLog.Payload(doc.ToString()));
		http.Response.StatusCode = StatusCodes.Status200OK;
		http.Response.ContentType = "application/xml; charset=utf-8";
		await http.Response.Body.WriteAsync(
			Encoding.UTF8.GetBytes(doc.Declaration + "\r\n" + doc), ct);
	}

	private static async Task<string?> ExtractEmailAsync(HttpContext http, ILogger wireLogger, CancellationToken ct)
	{
		if (!HttpMethods.IsPost(http.Request.Method) || http.Request.ContentLength is null or 0)
			return null;
		try
		{
			using StreamReader reader = new(http.Request.Body, Encoding.UTF8);
			string body = await reader.ReadToEndAsync(ct);
			if (wireLogger.IsEnabled(LogLevel.Trace))
				wireLogger.LogTrace("Autodiscover request: {Payload}", WireLog.Payload(body));
			if (string.IsNullOrWhiteSpace(body))
				return null;
			XDocument request = XDocument.Parse(body);
			// EMailAddress lives under Request; match by local name to tolerate schema/version drift.
			return request.Descendants()
				.FirstOrDefault(e => e.Name.LocalName == "EMailAddress")?.Value?.Trim();
		}
		catch (XmlException)
		{
			return null;
		}
	}

	private static string BuildEasUrl(HttpContext http, string? publicUrl)
	{
		// A configured PublicUrl wins: it never depends on client-supplied headers.
		if (!string.IsNullOrWhiteSpace(publicUrl))
			return publicUrl.TrimEnd('/') + EasEndpoint.Path;
		// Fallback: honour reverse-proxy headers so the advertised URL is the public one.
		// These are spoofable by the caller (they only affect the caller's own response);
		// set ActiveSync:PublicUrl to take them out of the picture entirely.
		string scheme = http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? http.Request.Scheme;
		string? host = http.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
		               ?? http.Request.Host.Value;
		return $"{scheme}://{host}{EasEndpoint.Path}";
	}
}
