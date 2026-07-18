using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Logging;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   Thin async WebDAV/CalDAV/CardDAV client over HttpClient: PROPFIND, REPORT, GET/PUT/DELETE
///   with ETag handling. Paths are server-absolute hrefs; the base URI supplies scheme/host.
/// </summary>
public sealed class WebDavClient : IDisposable
{
	private readonly Uri _baseUri;
	private readonly HttpClient _http;
	private readonly ILogger? _wireLogger;

	public WebDavClient(
		Uri baseUri,
		BackendCredentials credentials,
		bool allowInvalidCertificates = false,
		string? caCertificatePath = null,
		ILogger? wireLogger = null)
	{
		_baseUri = baseUri;
		_wireLogger = wireLogger;
		// Redirects are followed manually in SendAsync: HttpClient's auto-redirect strips
		// the Authorization header and downgrades non-GET methods on 301/302, which turns
		// a well-known discovery redirect (Stalwart: 307 → /dav/cal) into an
		// unauthenticated HTML page.
		SocketsHttpHandler handler = new()
		{
			AllowAutoRedirect = false,
			PooledConnectionLifetime = TimeSpan.FromMinutes(10)
		};
		RemoteCertificateValidationCallback? certCallback = ServerCertificateValidator.CreateCallback(
			allowInvalidCertificates, caCertificatePath);
		if (certCallback is not null)
			handler.SslOptions.RemoteCertificateValidationCallback = certCallback;
		_http = new HttpClient(handler)
		{
			Timeout = TimeSpan.FromSeconds(100)
		};
		string token = Convert.ToBase64String(
			Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
		_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
	}

	public void Dispose()
	{
		_http.Dispose();
	}

	public Uri Resolve(string href)
	{
		return new Uri(_baseUri, href);
	}

	public async Task<List<DavResource>> PropfindAsync(string href, int depth, XElement body, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(() =>
		{
			HttpRequestMessage request = new(new HttpMethod("PROPFIND"), Resolve(href));
			request.Headers.Add("Depth", depth.ToString());
			request.Content = XmlContent(new XDocument(body));
			return request;
		}, ct).ConfigureAwait(false);
		return await ReadMultiStatusAsync(response, ct).ConfigureAwait(false);
	}

	public async Task<List<DavResource>> ReportAsync(string href, int depth, XElement body, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(() =>
		{
			HttpRequestMessage request = new(new HttpMethod("REPORT"), Resolve(href));
			request.Headers.Add("Depth", depth.ToString());
			request.Content = XmlContent(new XDocument(body));
			return request;
		}, ct).ConfigureAwait(false);
		return await ReadMultiStatusAsync(response, ct).ConfigureAwait(false);
	}

	/// <summary>
	///   REPORT whose response is a raw (non-multistatus) body — CALDAV:free-busy-query
	///   answers text/calendar. Returns null on 401/403/404: no access or no such
	///   collection, which free/busy callers treat as "no data", not an error.
	/// </summary>
	public async Task<string?> ReportRawAsync(string href, int depth, XElement body, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(() =>
		{
			HttpRequestMessage request = new(new HttpMethod("REPORT"), Resolve(href));
			request.Headers.Add("Depth", depth.ToString());
			request.Content = XmlContent(new XDocument(body));
			return request;
		}, ct).ConfigureAwait(false);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
			return null;
		await EnsureSuccessAsync(response, "REPORT", href, ct).ConfigureAwait(false);
		return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
	}

	public async Task<(string Content, string? ETag)?> GetAsync(string href, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(
			() => new HttpRequestMessage(HttpMethod.Get, Resolve(href)), ct).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.NotFound)
			return null;
		await EnsureSuccessAsync(response, "GET", href, ct).ConfigureAwait(false);
		string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		return (content, response.Headers.ETag?.Tag);
	}

	/// <summary>PUT; pass etag null with ifNoneMatch=true for create-only semantics.</summary>
	public async Task<string?> PutAsync(
		string href, string content, string contentType, string? etag, bool ifNoneMatch, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(() =>
		{
			HttpRequestMessage request = new(HttpMethod.Put, Resolve(href))
			{
				Content = new StringContent(content, Encoding.UTF8, contentType)
			};
			if (ifNoneMatch)
				request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
			else if (etag is not null && EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? parsed))
				request.Headers.IfMatch.Add(parsed);
			return request;
		}, ct).ConfigureAwait(false);
		await EnsureSuccessAsync(response, "PUT", href, ct).ConfigureAwait(false);
		return response.Headers.ETag?.Tag;
	}

	public async Task DeleteAsync(string href, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(
			() => new HttpRequestMessage(HttpMethod.Delete, Resolve(href)), ct).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.NotFound)
			return;
		await EnsureSuccessAsync(response, "DELETE", href, ct).ConfigureAwait(false);
	}

	/// <summary>
	///   The resource's compliance classes (the OPTIONS "DAV:" header, comma-joined,
	///   lowercase) — e.g. "1, calendar-access, calendar-auto-schedule". Empty on failure.
	/// </summary>
	public async Task<string> GetDavCapabilitiesAsync(string href, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(
			() => new HttpRequestMessage(HttpMethod.Options, Resolve(href)), ct).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			return "";
		return response.Headers.TryGetValues("DAV", out IEnumerable<string>? values)
			? string.Join(", ", values).ToLowerInvariant()
			: "";
	}

	/// <summary>
	///   Sends a request, following same-host redirects manually with the method, body and
	///   Authorization header intact (auto-redirect would strip auth and downgrade methods).
	///   The factory is invoked once per hop because HttpRequestMessage is single-use.
	/// </summary>
	private async Task<HttpResponseMessage> SendAsync(
		Func<HttpRequestMessage> createRequest, CancellationToken ct)
	{
		bool trace = _wireLogger?.IsEnabled(LogLevel.Trace) == true;
		Uri? redirectTarget = null;
		for (int hop = 0;; hop++)
		{
			HttpRequestMessage request = createRequest();
			if (redirectTarget is not null)
				request.RequestUri = redirectTarget;
			Uri currentUri = request.RequestUri!; // the URI actually requested this hop
			string method = request.Method.Method;
			// Verbose wire logging — method, URI and body only, NEVER headers (the
			// Authorization header must stay out of the logs by construction).
			if (trace)
				_wireLogger!.LogTrace("{Method} {Uri} request: {Payload}",
					method, currentUri,
					request.Content is null
						? "(no body)"
						: WireLog.Payload(await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
			HttpResponseMessage response;
			try
			{
				response = await _http.SendAsync(request, ct).ConfigureAwait(false);
			}
			finally
			{
				request.Dispose();
			}

			if ((int)response.StatusCode is not (301 or 302 or 307 or 308) || hop >= 5)
			{
				if (trace)
				{
					// Buffer so the caller's own read still works afterwards.
					await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
					_wireLogger!.LogTrace("{StatusCode} {Status} for {Method} {Uri}: {Payload}",
						(int)response.StatusCode, response.StatusCode, method, currentUri,
						WireLog.Payload(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
				}

				return response;
			}
			Uri? location = response.Headers.Location;
			if (location is null)
				return response;
			// Resolve a relative Location against the CURRENT hop, not the original base.
			Uri target = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
			if (!IsSafeRedirect(_baseUri, target))
				return response;
			response.Dispose();
			redirectTarget = target;
		}
	}

	/// <summary>
	///   A redirect is followed (with the Basic Authorization header attached) only when it
	///   stays on the same origin — identical scheme, host AND port. Any other target (a
	///   different port, or an https→http downgrade) could hand the credentials to another
	///   service or put them on the wire in cleartext, so the redirect is not followed.
	/// </summary>
	public static bool IsSafeRedirect(Uri baseUri, Uri target)
	{
		return string.Equals(target.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
		       string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
		       target.Port == baseUri.Port;
	}

	public async Task<string?> GetPropertyAsync(string href, XName property, CancellationToken ct)
	{
		XElement body = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop", new XElement(property)));
		List<DavResource> resources = await PropfindAsync(href, 0, body, ct).ConfigureAwait(false);
		return resources
			.Select(r => r.Propstat.Descendants(property).FirstOrDefault()?.Value)
			.FirstOrDefault(v => v is not null);
	}

	private static StringContent XmlContent(XDocument doc)
	{
		return new StringContent(
			doc.Declaration is null ? "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + doc : doc.ToString(),
			Encoding.UTF8, "application/xml");
	}

	private static async Task<List<DavResource>> ReadMultiStatusAsync(
		HttpResponseMessage response, CancellationToken ct)
	{
		if (response.StatusCode is HttpStatusCode.NotFound)
			return [];
		if ((int)response.StatusCode != 207 && !response.IsSuccessStatusCode)
			// The response body can contain contact/calendar PII (or an HTML login page) and
			// this message reaches the logs — keep only the status.
			throw new BackendException(
				$"DAV request failed: {(int)response.StatusCode} {response.ReasonPhrase}.");

		string xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		XDocument doc;
		try
		{
			doc = XDocument.Parse(xml);
		}
		catch (XmlException ex)
		{
			// e.g. an HTML error/login page — surface as a backend error, not a crash. The
			// body is omitted from the message (it may carry PII and is logged).
			throw new BackendException(
				$"DAV response was not valid XML ({(int)response.StatusCode} {response.ReasonPhrase}).", ex);
		}

		List<DavResource> result = new();
		foreach (XElement responseElement in doc.Descendants(DavNs.D + "response"))
		{
			string? href = responseElement.Element(DavNs.D + "href")?.Value;
			if (href is null)
				continue;
			XElement? okPropstat = responseElement.Elements(DavNs.D + "propstat")
				.FirstOrDefault(p => p.Element(DavNs.D + "status")?.Value.Contains("200") != false);
			if (okPropstat is not null)
				result.Add(new DavResource(Uri.UnescapeDataString(href), okPropstat));
		}

		return result;
	}

	private static async Task EnsureSuccessAsync(
		HttpResponseMessage response, string method, string href, CancellationToken ct)
	{
		if (response.IsSuccessStatusCode)
			return;
		if (response.StatusCode == HttpStatusCode.PreconditionFailed)
			throw new BackendException($"DAV {method} {href}: precondition failed (ETag conflict).");
		// Body omitted from the message — it may contain PII and this reaches the logs.
		throw new BackendException(
			$"DAV {method} {href} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
	}
}
