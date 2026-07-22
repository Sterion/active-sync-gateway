using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
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
		// Redirects are followed manually in SendAsync: HttpClient's auto-redirect strips the
		// Authorization header and downgrades non-GET methods on 301/302, which turns a
		// well-known discovery redirect (Stalwart: 307 → /dav/cal) into an unauthenticated HTML
		// page. BackendHttpClientFactory builds the handler (no auto-redirect) and Basic auth.
		: this(baseUri,
			BackendHttpClientFactory.CreateClient(credentials, allowInvalidCertificates, caCertificatePath),
			wireLogger)
	{
	}

	/// <summary>Test seam: inject a pre-built <see cref="HttpClient" /> (e.g. over a stub handler).</summary>
	internal WebDavClient(Uri baseUri, HttpClient http, ILogger? wireLogger = null)
	{
		_baseUri = baseUri;
		_wireLogger = wireLogger;
		_http = http;
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
			else if (etag is not null && BuildIfMatch(etag) is { } ifMatch)
				request.Headers.IfMatch.Add(ifMatch);
			return request;
		}, ct).ConfigureAwait(false);

		// A create-PUT (If-None-Match:*) that comes back "already exists" means the resource is
		// present at this href. The transient retry above replays a PUT whose response was lost, so
		// if the first attempt actually reached the server the replay lands on the resource it just
		// created — the create SUCCEEDED. Surfacing the failure would tell the client the item wasn't
		// created though it was; instead treat it as success and let the caller (CreateItemAsync ->
		// ResolveStoredHrefAsync, which re-reads the collection) adopt the stored href/ETag.
		// Servers disagree on the status for a failed If-None-Match:*: Stalwart answers 412
		// Precondition Failed (RFC 7232), Axigen answers 409 Conflict — accept both. An update-PUT
		// uses If-Match, so its 412/409 (a real ETag conflict / lost update) still surfaces below;
		// this reinterpretation is gated on ifNoneMatch (create-only). (H18)
		if (ifNoneMatch &&
			response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
		{
			_wireLogger?.LogDebug(
				"DAV create-PUT {Href} returned {Status} (already exists — a replayed create); treating as success",
				href, (int)response.StatusCode);
			return null;
		}

		await EnsureSuccessAsync(response, "PUT", href, ct).ConfigureAwait(false);
		return response.Headers.ETag?.Tag;
	}

	/// <summary>
	///   Builds an <c>If-Match</c> value from a stored ETag. Servers routinely hand back a bare,
	///   unquoted ETag; <see cref="EntityTagHeaderValue.TryParse" /> rejects that, so the old code
	///   silently omitted the header and issued an unconditional PUT — a lost update (H3). A tag
	///   that already parses is used as-is (preserving weak/strong); otherwise it is normalized to
	///   a quoted strong (or weak, for a "W/" prefix) tag.
	/// </summary>
	internal static EntityTagHeaderValue BuildIfMatch(string etag)
	{
		string value = etag.Trim();
		if (EntityTagHeaderValue.TryParse(value, out EntityTagHeaderValue? parsed))
			return parsed;
		bool weak = value.StartsWith("W/", StringComparison.Ordinal);
		string tag = (weak ? value[2..] : value).Trim();
		if (tag.Length >= 2 && tag[0] == '"' && tag[^1] == '"')
			tag = tag[1..^1];
		tag = tag.Replace("\"", ""); // an entity tag cannot carry a raw quote; drop any stray ones
		return new EntityTagHeaderValue($"\"{tag}\"", weak);
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
	///   Every DAV verb funnels through here, so fast transient retry lives at this one seam. All
	///   DAV writes are idempotent — create-PUT carries If-None-Match:* (a replay 412s or 409s, never
	///   duplicates), update-PUT carries If-Match, DELETE treats 404 as success — and the rest are
	///   reads, so a replay is always safe.
	/// </summary>
	private Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
	{
		return TransientRetry.SendHttpAsync(
			() => SendFollowingRedirectsAsync(createRequest, ct), ct, idempotent: true,
			onRetry: (reason, attempt) =>
			{
				Core.Observability.GatewayMetrics.RecordBackendRetry("dav");
				_wireLogger?.LogDebug("DAV request transient failure ({Reason}); retry {Attempt}/{Max}",
					reason, attempt, TransientRetry.DelaysMs.Length);
			});
	}

	/// <summary>
	///   Sends a request, following same-host redirects manually with the method, body and
	///   Authorization header intact (auto-redirect would strip auth and downgrade methods).
	///   The factory is invoked once per hop because HttpRequestMessage is single-use.
	/// </summary>
	private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
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

	private async Task<List<DavResource>> ReadMultiStatusAsync(
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
			doc = ParseHardenedXml(xml);
		}
		catch (XmlException ex)
		{
			// e.g. an HTML error/login page — surface as a backend error, not a crash. The
			// body is omitted from the message (it may carry PII and is logged).
			throw new BackendException(
				$"DAV response was not valid XML ({(int)response.StatusCode} {response.ReasonPhrase}).", ex);
		}

		List<DavResource> result = new();
		int droppedFailures = 0;
		foreach (XElement responseElement in doc.Descendants(DavNs.D + "response"))
		{
			string? href = responseElement.Element(DavNs.D + "href")?.Value;
			if (href is null)
				continue;
			// H27: select the propstat by its actual 2xx status code, not a fragile
			// substring match on "200" (which also treated a status-less propstat as OK and
			// dropped a legitimate 2xx such as 204).
			XElement? okPropstat = responseElement.Elements(DavNs.D + "propstat")
				.FirstOrDefault(p => IsOkStatus(p.Element(DavNs.D + "status")?.Value));
			if (okPropstat is not null)
				// H2: keep the href exactly as the server percent-encoded it. It is used verbatim
				// as a request path (Resolve → new Uri(base, href)); unescaping it here turned a
				// resource named "a#b.ics" into path "/…/a" with "#b.ics" as a URI fragment, so
				// every GET/PUT/DELETE hit the wrong resource. Href comparison against share grants
				// unescapes on its own side (SharedHrefEquals), so it does not depend on this.
				result.Add(new DavResource(href, okPropstat));
			else
				// H27: a <response> with no 2xx propstat is a per-resource failure inside an
				// otherwise-207 multistatus (403/404/507…). It used to vanish without a trace,
				// hiding a permission/quota problem behind a "shorter than expected" listing.
				// Log the status codes only — the href can carry PII.
				droppedFailures++;
		}

		if (droppedFailures > 0)
			_wireLogger?.LogDebug(
				"DAV multistatus carried {DroppedCount} per-resource failure response(s) with no 2xx propstat; " +
				"they were omitted from the {ReturnedCount} returned resource(s)", droppedFailures, result.Count);

		return result;
	}

	/// <summary>
	///   Parses a DAV multistatus body with external-entity resolution and DTD processing
	///   explicitly disabled (H28). <see cref="XDocument.Parse(string)" /> already prohibits DTDs
	///   by default, so this is not a behaviour change — it makes the XXE hardening a stated,
	///   review-visible property of the code rather than a silent inheritance of a framework
	///   default that a future refactor could flip. A backend response is attacker-adjacent
	///   (a compromised or hostile DAV server), so it must never resolve an external entity.
	/// </summary>
	private static XDocument ParseHardenedXml(string xml)
	{
		XmlReaderSettings settings = new()
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			MaxCharactersFromEntities = 0
		};
		using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);
		return XDocument.Load(reader);
	}

	/// <summary>True when a DAV propstat status line ("HTTP/1.1 200 OK") reports a 2xx code.</summary>
	private static bool IsOkStatus(string? statusLine)
	{
		if (string.IsNullOrWhiteSpace(statusLine))
			return false;
		foreach (string token in statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			if (token.Length == 3 && int.TryParse(token, out int code))
				return code is >= 200 and < 300;
		return false;
	}

	private static async Task EnsureSuccessAsync(
		HttpResponseMessage response, string method, string href, CancellationToken ct)
	{
		if (response.IsSuccessStatusCode)
			return;
		Core.Observability.GatewayMetrics.RecordBackendError("dav");
		if (response.StatusCode == HttpStatusCode.PreconditionFailed)
			throw new BackendException($"DAV {method} {href}: precondition failed (ETag conflict).");
		// Body omitted from the message — it may contain PII and this reaches the logs.
		throw new BackendException(
			$"DAV {method} {href} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
	}
}
