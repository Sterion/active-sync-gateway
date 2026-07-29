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
	/// <summary>
	///   Ceiling on a single DAV response body. Multistatus listings for a
	///   large collection are legitimately several MB, so the cap is generous — its job is to bound a
	///   malicious or malfunctioning server that would otherwise stream an unbounded body into memory,
	///   not to second-guess a real listing. Deliberately decoupled from and lower than
	///   <see cref="BackendHttpClientFactory.MaxBackendResponseBytes" /> (128 MiB): that shared ceiling
	///   exists for JMAP's legitimate large blob/attachment downloads, but a DAV multistatus response
	///   is never remotely that large, and XDocument's in-memory tree runs 5-10x the wire size — a
	///   hostile 128 MB multistatus would amplify to roughly 1 GB of managed heap. Exposed internally
	///   so tests can lower it further.
	/// </summary>
	internal static readonly long DefaultMaxResponseBytes = 32L * 1024 * 1024;

	private readonly Uri _baseUri;
	private readonly HttpClient _http;
	private readonly ILogger? _wireLogger;
	private readonly RedirectingHttpSender _redirectSender;

	/// <summary>Per-response size ceiling; see <see cref="DefaultMaxResponseBytes" />.</summary>
	internal long MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

	/// <summary>
	///   Ceiling on XML character count during parse — decoupled from
	///   <see cref="MaxResponseBytes" />: a response can be well within the byte ceiling and still
	///   expand into an excessive character count once XDocument materializes an XElement/XAttribute/
	///   XText per node. A few million is far above any real multistatus listing. Exposed internally
	///   so tests can lower it.
	/// </summary>
	internal long MaxCharactersInDocument { get; set; } = DefaultMaxCharactersInDocument;

	private const long DefaultMaxCharactersInDocument = 8_000_000;

	public WebDavClient(
		Uri baseUri,
		BackendCredentials credentials,
		bool allowInvalidCertificates = false,
		string? caCertificatePath = null,
		ILogger? wireLogger = null,
		bool checkRevocation = false)
		// Redirects are followed manually in SendAsync: HttpClient's auto-redirect strips the
		// Authorization header and downgrades non-GET methods on 301/302, which turns a
		// well-known discovery redirect (Stalwart: 307 → /dav/cal) into an unauthenticated HTML
		// page. BackendHttpClientFactory builds the handler (no auto-redirect) and Basic auth.
		: this(baseUri,
			BackendHttpClientFactory.CreateClient(
				credentials, allowInvalidCertificates, caCertificatePath, checkRevocation: checkRevocation),
			wireLogger)
	{
	}

	/// <summary>Test seam: inject a pre-built <see cref="HttpClient" /> (e.g. over a stub handler).</summary>
	internal WebDavClient(Uri baseUri, HttpClient http, ILogger? wireLogger = null)
	{
		_baseUri = baseUri;
		_wireLogger = wireLogger;
		_http = http;
		_redirectSender = new RedirectingHttpSender(_http, _baseUri, _wireLogger);
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
		return await ReadCappedStringAsync(response, "REPORT", href, ct).ConfigureAwait(false);
	}

	public async Task<(string Content, string? ETag)?> GetAsync(string href, CancellationToken ct)
	{
		using HttpResponseMessage response = await SendAsync(
			() => new HttpRequestMessage(HttpMethod.Get, Resolve(href)), ct).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.NotFound)
			return null;
		await EnsureSuccessAsync(response, "GET", href, ct).ConfigureAwait(false);
		string content = await ReadCappedStringAsync(response, "GET", href, ct).ConfigureAwait(false);
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
		// Precondition Failed (RFC 7232) — unambiguously the If-* precondition itself, so no further
		// check is needed. This reinterpretation is gated on ifNoneMatch (create-only); an
		// update-PUT's own 412 handling (a real ETag conflict OR a lost-response replay) is
		// below.
		if (ifNoneMatch && response.StatusCode is HttpStatusCode.PreconditionFailed)
		{
			_wireLogger?.LogDebug(
				"DAV create-PUT {Href} returned {Status} (already exists — a replayed create); treating as success",
				href, (int)response.StatusCode);
			return null;
		}

		// Axigen answers the identical replayed-create condition with 409 Conflict instead of 412.
		// Unlike 412, RFC 4918 §9.7.1 defines 409 on PUT as "the parent collection does not exist" —
		// it is NOT inherently "already exists", so blindly reinterpreting every create-PUT 409 as
		// success hid a genuine failure: a collection deleted/renamed server-side while the session
		// was cached. Narrow it: only accept the replay reading when a GET confirms the target is
		// actually there.
		if (ifNoneMatch && response.StatusCode is HttpStatusCode.Conflict &&
			await GetAsync(href, ct).ConfigureAwait(false) is not null)
		{
			_wireLogger?.LogDebug(
				"DAV create-PUT {Href} returned 409 Conflict and the target exists (a replayed create); " +
				"treating as success",
				href);
			return null;
		}

		// Every DAV verb funnels through the same fast transient retry (SendAsync). If an
		// update-PUT's write actually landed but its response was lost (a reset, or a 503 from a
		// load balancer after the write), the retry replays the SAME If-Match header — which the
		// first attempt's own write has already invalidated, so the replay genuinely 412s even
		// though the server accepted the change. Re-GETting the resource and finding its content
		// already matches exactly what we just tried to write distinguishes that lost-response
		// replay from a real concurrent conflict (someone else's edit, or a stale client cache),
		// which keeps surfacing as a failure below.
		if (!ifNoneMatch && response.StatusCode is HttpStatusCode.PreconditionFailed &&
			await GetAsync(href, ct).ConfigureAwait(false) is { } existing &&
			string.Equals(existing.Content, content, StringComparison.Ordinal))
		{
			_wireLogger?.LogDebug(
				"DAV update-PUT {Href} returned 412 but the stored content already matches (a replayed update); " +
				"treating as success",
				href);
			return existing.ETag;
		}

		await EnsureSuccessAsync(response, "PUT", href, ct).ConfigureAwait(false);
		return response.Headers.ETag?.Tag;
	}

	/// <summary>
	///   Builds an <c>If-Match</c> value from a stored ETag. Servers routinely hand back a bare,
	///   unquoted ETag; <see cref="EntityTagHeaderValue.TryParse" /> rejects that, so the old code
	///   silently omitted the header and issued an unconditional PUT — a lost update. A tag
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
	private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
	{
		try
		{
			return await TransientRetry.SendHttpAsync(
				() => _redirectSender.SendAsync(createRequest, ct), ct, idempotent: true,
				onRetry: (reason, attempt) =>
				{
					Core.Observability.GatewayMetrics.RecordBackendRetry("dav");
					_wireLogger?.LogDebug("DAV request transient failure ({Reason}); retry {Attempt}/{Max}",
						reason, attempt, TransientRetry.DelaysMs.Length);
				}).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException)
		{
			// TransientRetry rethrows the ORIGINAL transport exception once its retry budget is
			// spent — every DAV call site funnels through this one seam, but several of them (the
			// shared-collection probe, the ctag poll, FindByUidAsync, the CardDAV GAL fallback) only
			// ever catch BackendException, so a raw HttpRequestException/IOException escaped every
			// "never break folder sync / never treat a hiccup as a change" guard built on that catch.
			// Every other DAV failure mode (HTTP status, XML parse) already surfaces as
			// BackendException; wrapping here — the single seam every verb funnels through — fixes
			// every call site at once instead of widening each catch individually.
			Core.Observability.GatewayMetrics.RecordBackendError("dav");
			throw new BackendException($"DAV request failed: {ex.Message}", ex);
		}
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

		XDocument doc;
		try
		{
			doc = await ParseHardenedXmlAsync(response, "multistatus", ct).ConfigureAwait(false);
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
			// Select the propstat by its actual 2xx status code, not a fragile
			// substring match on "200" (which also treated a status-less propstat as OK and
			// dropped a legitimate 2xx such as 204).
			XElement? okPropstat = responseElement.Elements(DavNs.D + "propstat")
				.FirstOrDefault(p => IsOkStatus(p.Element(DavNs.D + "status")?.Value));
			if (okPropstat is not null)
				// Keep the href exactly as the server percent-encoded it. It is used verbatim
				// as a request path (Resolve → new Uri(base, href)); unescaping it here turned a
				// resource named "a#b.ics" into path "/…/a" with "#b.ics" as a URI fragment, so
				// every GET/PUT/DELETE hit the wrong resource. Href comparison against share grants
				// unescapes on its own side (SharedHrefEquals), so it does not depend on this.
				result.Add(new DavResource(href, okPropstat));
			else
				// A <response> with no 2xx propstat is a per-resource failure inside an
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
	///   Parses a DAV multistatus response by STREAMING it with external-entity resolution and
	///   DTD processing explicitly disabled. The body is read through a size-capped stream so a
	///   malicious/malfunctioning server cannot buffer an unbounded response into memory; the XXE
	///   hardening (DtdProcessing.Prohibit, XmlResolver null) is a stated, review-visible property
	///   rather than a silent inheritance of a framework default a future refactor could flip.
	/// </summary>
	private async Task<XDocument> ParseHardenedXmlAsync(
		HttpResponseMessage response, string what, CancellationToken ct)
	{
		await using Stream stream = await OpenCappedStreamAsync(response, what, ct).ConfigureAwait(false);
		XmlReaderSettings settings = new()
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			MaxCharactersFromEntities = 0,
			MaxCharactersInDocument = MaxCharactersInDocument,
			Async = true
		};
		using XmlReader reader = XmlReader.Create(stream, settings);
		return await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
	}

	/// <summary>Reads a (non-multistatus) response body as a string through the same size cap.</summary>
	private async Task<string> ReadCappedStringAsync(
		HttpResponseMessage response, string method, string href, CancellationToken ct)
	{
		await using Stream stream = await OpenCappedStreamAsync(response, $"{method} {href}", ct).ConfigureAwait(false);
		using StreamReader reader = new(stream, Encoding.UTF8);
		return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	///   Opens the response content stream behind a hard byte ceiling. A declared
	///   Content-Length over the ceiling is rejected before a single byte is read; a chunked body
	///   with no declared length is capped mid-read by <see cref="LengthCapStream" /> so it cannot
	///   grow without bound either.
	/// </summary>
	private async Task<Stream> OpenCappedStreamAsync(
		HttpResponseMessage response, string what, CancellationToken ct)
	{
		if (response.Content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
			throw new BackendException(
				$"DAV {what} response is {declared} bytes, over the {MaxResponseBytes}-byte ceiling.");
		Stream raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		return new LengthCapStream(raw, MaxResponseBytes, what);
	}

	/// <summary>
	///   A forward-only read wrapper that throws <see cref="BackendException" /> once the total bytes
	///   read exceed the ceiling — the backstop for a response whose size is not declared up front.
	/// </summary>
	private sealed class LengthCapStream(Stream inner, long cap, string what) : Stream
	{
		private long _read;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => _read; set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count)
		{
			return Track(inner.Read(buffer, offset, count));
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
		{
			return Track(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
		{
			return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
		}

		private int Track(int n)
		{
			_read += n;
			if (_read > cap)
				throw new BackendException($"DAV {what} response exceeded the {cap}-byte ceiling mid-stream.");
			return n;
		}

		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				inner.Dispose();
			base.Dispose(disposing);
		}
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
		// Typed, so the host can tell "the item moved underneath the merge" from any other backend
		// error: an update-PUT carrying the contract's `expected` revision as If-Match answers 412
		// exactly when that precondition failed, and the host then re-fetches, re-merges and retries
		// once. BackendPreconditionFailedException derives from BackendException, so every existing
		// `catch (BackendException)` guard still funnels it.
		if (response.StatusCode == HttpStatusCode.PreconditionFailed)
			throw new BackendPreconditionFailedException($"DAV {method} {href}: precondition failed (ETag conflict).");
		// Body omitted from the message — it may contain PII and this reaches the logs.
		throw new BackendException(
			$"DAV {method} {href} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
	}
}
