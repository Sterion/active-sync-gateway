using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Observability;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Jmap;

/// <summary>Well-known JMAP capability URNs (RFC 8620/8621 + drafts).</summary>
public static class JmapCapabilities
{
	public const string Core = "urn:ietf:params:jmap:core";
	public const string Mail = "urn:ietf:params:jmap:mail";
	public const string Submission = "urn:ietf:params:jmap:submission";
	public const string VacationResponse = "urn:ietf:params:jmap:vacationresponse";
	public const string Blob = "urn:ietf:params:jmap:blob";
	public const string Calendars = "urn:ietf:params:jmap:calendars";
	public const string Contacts = "urn:ietf:params:jmap:contacts";
}

/// <summary>One JMAP method call: [name, arguments, callId] on the wire.</summary>
public sealed record JmapCall(string Name, IReadOnlyDictionary<string, object?> Arguments, string Id);

/// <summary>Raised when the JMAP server rejects the presented credentials (HTTP 401/403).</summary>
public sealed class JmapAuthenticationException(string message) : Exception(message);

/// <summary>
///   The parsed session resource, with the server's advertised api/download/upload URLs
///   re-anchored onto the base URL the gateway actually connected to (a server behind a
///   proxy or on a container network advertises URLs on its own hostname, unreachable from
///   here — only the path/template is authoritative).
/// </summary>
public sealed record JmapSessionResource(
	Uri ApiUrl,
	string DownloadUrlTemplate,
	string UploadUrlTemplate,
	string? EventSourceUrlTemplate,
	IReadOnlyDictionary<string, string> PrimaryAccounts,
	IReadOnlySet<string> Capabilities,
	string State)
{
	public bool HasCapability(string urn) => Capabilities.Contains(urn);

	public string PrimaryAccount(string capabilityUrn)
	{
		return PrimaryAccounts.TryGetValue(capabilityUrn, out string? id)
			? id
			: throw new BackendException($"JMAP server exposes no primary account for {capabilityUrn}.");
	}
}

/// <summary>
///   The parsed <c>methodResponses</c> of one JMAP request. Owns the underlying document, so
///   callers dispose it once they have read the results they need.
/// </summary>
public sealed class JmapResponse(JsonDocument document) : IDisposable
{
	/// <summary>Arguments of the response with the given call id; throws on a method-level error.</summary>
	public JsonElement Arguments(string callId)
	{
		foreach (JsonElement mr in document.RootElement.GetProperty("methodResponses").EnumerateArray())
		{
			if (mr.GetArrayLength() < 3 || mr[2].GetString() != callId)
				continue;
			string name = mr[0].GetString() ?? "";
			if (name == "error")
			{
				// The error "type" is a spec token (e.g. invalidArguments) — safe to log; the
				// description may echo input, so it is deliberately omitted.
				string type = mr[1].TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
				throw new BackendException($"JMAP method '{callId}' failed: {type}.");
			}

			return mr[1];
		}

		throw new BackendException($"JMAP response carried no result for call '{callId}'.");
	}

	public void Dispose() => document.Dispose();
}

/// <summary>
///   Thin JMAP client over HttpClient + System.Text.Json: session discovery, batched method
///   invocation with back-reference support, and blob upload/download. One instance per
///   backend session; the session resource is fetched once and cached. HttpClient is
///   thread-safe, so no per-call gate is needed.
/// </summary>
public sealed class JmapClient : IDisposable
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		// Keys are literal JMAP names built by callers — no naming policy, and nulls are
		// significant in JMAP (ids:null = "all"; a null patch value = "remove").
		PropertyNamingPolicy = null
	};

	private readonly Uri _baseUri;
	private readonly HttpClient _http;
	private readonly ILogger? _wireLogger;
	private readonly SemaphoreSlim _sessionGate = new(1, 1);
	private JmapSessionResource? _session;

	public JmapClient(
		Uri baseUri,
		BackendCredentials credentials,
		bool allowInvalidCertificates = false,
		string? caCertificatePath = null,
		ILogger? wireLogger = null)
		: this(baseUri, BuildHttpClient(credentials, allowInvalidCertificates, caCertificatePath), wireLogger)
	{
	}

	/// <summary>Test seam: inject a pre-built <see cref="HttpClient" /> (e.g. over a stub handler).</summary>
	internal JmapClient(Uri baseUri, HttpClient http, ILogger? wireLogger = null)
	{
		_baseUri = baseUri;
		_http = http;
		_wireLogger = wireLogger;
	}

	private static HttpClient BuildHttpClient(
		BackendCredentials credentials, bool allowInvalidCertificates, string? caCertificatePath)
	{
		// Redirects are followed manually (same-origin only) so the Authorization header is
		// never handed to another origin — the .well-known/jmap discovery hop redirects to
		// the real session resource, and HttpClient's auto-redirect strips auth on it.
		SocketsHttpHandler handler = new()
		{
			AllowAutoRedirect = false,
			PooledConnectionLifetime = TimeSpan.FromMinutes(10)
		};
		RemoteCertificateValidationCallback? certCallback = ServerCertificateValidator.CreateCallback(
			allowInvalidCertificates, caCertificatePath);
		if (certCallback is not null)
			handler.SslOptions.RemoteCertificateValidationCallback = certCallback;
		HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(100) };
		string token = Convert.ToBase64String(
			Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
		http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
		return http;
	}

	public void Dispose()
	{
		_http.Dispose();
		_sessionGate.Dispose();
	}

	/// <summary>Fetches (and caches) the session resource, re-anchoring its URLs onto the base URL.</summary>
	public async Task<JmapSessionResource> GetSessionAsync(CancellationToken ct)
	{
		if (_session is not null)
			return _session;
		await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (_session is not null)
				return _session;
			Uri wellKnown = new(_baseUri, "/.well-known/jmap");
			using HttpResponseMessage response = await SendAsync(
				() => new HttpRequestMessage(HttpMethod.Get, wellKnown), ct).ConfigureAwait(false);
			await EnsureSuccessAsync(response, "GET", "session", ct).ConfigureAwait(false);
			string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
			_session = ParseSession(body);
			return _session;
		}
		finally
		{
			_sessionGate.Release();
		}
	}

	/// <summary>Invokes one or more method calls in a single request; caller disposes the result.</summary>
	public async Task<JmapResponse> InvokeAsync(
		IReadOnlyList<string> capabilities, IReadOnlyList<JmapCall> calls, CancellationToken ct)
	{
		JmapSessionResource session = await GetSessionAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> payload = new()
		{
			["using"] = capabilities,
			["methodCalls"] = calls.Select(c => new object?[] { c.Name, c.Arguments, c.Id }).ToArray()
		};
		string json = JsonSerializer.Serialize(payload, SerializerOptions);
		using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, session.ApiUrl)
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json")
		}, ct, idempotent: AllReadOnly(calls)).ConfigureAwait(false);
		await EnsureSuccessAsync(response, "POST", "api", ct).ConfigureAwait(false);
		string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		try
		{
			return new JmapResponse(JsonDocument.Parse(responseBody));
		}
		catch (JsonException ex)
		{
			GatewayMetrics.RecordBackendError("jmap");
			throw new BackendException("JMAP response was not valid JSON.", ex);
		}
	}

	/// <summary>Convenience: one method call, returning that call's result arguments.</summary>
	public async Task<JmapResponse> CallAsync(
		IReadOnlyList<string> capabilities, string method, IReadOnlyDictionary<string, object?> arguments,
		CancellationToken ct)
	{
		return await InvokeAsync(capabilities, [new JmapCall(method, arguments, "0")], ct).ConfigureAwait(false);
	}

	public async Task<byte[]> DownloadBlobAsync(string accountId, string blobId, CancellationToken ct)
	{
		JmapSessionResource session = await GetSessionAsync(ct).ConfigureAwait(false);
		string url = session.DownloadUrlTemplate
			.Replace("{accountId}", Uri.EscapeDataString(accountId))
			.Replace("{blobId}", Uri.EscapeDataString(blobId))
			.Replace("{name}", "blob")
			.Replace("{type}", Uri.EscapeDataString("application/octet-stream"));
		using HttpResponseMessage response = await SendAsync(
			() => new HttpRequestMessage(HttpMethod.Get, new Uri(url)), ct).ConfigureAwait(false);
		await EnsureSuccessAsync(response, "GET", "download", ct).ConfigureAwait(false);
		return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	///   Opens the JMAP EventSource (SSE) stream, or null when the server advertises none. The
	///   caller owns the returned response and reads its content stream line by line.
	/// </summary>
	public async Task<HttpResponseMessage?> OpenEventSourceAsync(int pingSeconds, CancellationToken ct)
	{
		JmapSessionResource session = await GetSessionAsync(ct).ConfigureAwait(false);
		if (session.EventSourceUrlTemplate is not { } template)
			return null;
		string url = template
			.Replace("{types}", "*")
			.Replace("{closeafter}", "no")
			.Replace("{ping}", pingSeconds.ToString());
		HttpRequestMessage request = new(HttpMethod.Get, new Uri(url));
		request.Headers.Accept.ParseAdd("text/event-stream");
		HttpResponseMessage response = await _http
			.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		await EnsureSuccessAsync(response, "GET", "eventsource", ct).ConfigureAwait(false);
		return response;
	}

	/// <summary>Uploads a blob and returns its server-assigned blobId.</summary>
	public async Task<string> UploadBlobAsync(string accountId, byte[] content, string contentType, CancellationToken ct)
	{
		JmapSessionResource session = await GetSessionAsync(ct).ConfigureAwait(false);
		string url = session.UploadUrlTemplate.Replace("{accountId}", Uri.EscapeDataString(accountId));
		using HttpResponseMessage response = await SendAsync(() =>
		{
			ByteArrayContent payload = new(content);
			payload.Headers.ContentType = new MediaTypeHeaderValue(contentType);
			return new HttpRequestMessage(HttpMethod.Post, new Uri(url)) { Content = payload };
		}, ct).ConfigureAwait(false);
		await EnsureSuccessAsync(response, "POST", "upload", ct).ConfigureAwait(false);
		string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		using JsonDocument doc = JsonDocument.Parse(body);
		return doc.RootElement.TryGetProperty("blobId", out JsonElement blobId)
			? blobId.GetString() ?? throw new BackendException("JMAP upload returned a null blobId.")
			: throw new BackendException("JMAP upload response carried no blobId.");
	}

	private JmapSessionResource ParseSession(string body)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(body);
		}
		catch (JsonException ex)
		{
			throw new BackendException("JMAP session resource was not valid JSON.", ex);
		}

		using (doc)
		{
			JsonElement root = doc.RootElement;
			string apiUrl = RequireString(root, "apiUrl");
			string downloadUrl = RequireString(root, "downloadUrl");
			string uploadUrl = RequireString(root, "uploadUrl");
			string? eventSourceUrl = root.TryGetProperty("eventSourceUrl", out JsonElement es) &&
			                         es.GetString() is { Length: > 0 } esUrl
				? Rebase(esUrl)
				: null;

			Dictionary<string, string> primary = new(StringComparer.Ordinal);
			if (root.TryGetProperty("primaryAccounts", out JsonElement pa) && pa.ValueKind == JsonValueKind.Object)
				foreach (JsonProperty p in pa.EnumerateObject())
					if (p.Value.GetString() is { } accountId)
						primary[p.Name] = accountId;

			HashSet<string> capabilities = new(StringComparer.Ordinal);
			if (root.TryGetProperty("capabilities", out JsonElement caps) && caps.ValueKind == JsonValueKind.Object)
				foreach (JsonProperty p in caps.EnumerateObject())
					capabilities.Add(p.Name);

			string state = root.TryGetProperty("state", out JsonElement s) ? s.GetString() ?? "" : "";

			return new JmapSessionResource(
				new Uri(Rebase(apiUrl)), Rebase(downloadUrl), Rebase(uploadUrl), eventSourceUrl,
				primary, capabilities, state);
		}
	}

	/// <summary>
	///   Swaps an advertised absolute URL's origin (scheme://host:port) for the base URL's,
	///   leaving the path and any {template} placeholders in the query untouched.
	/// </summary>
	private string Rebase(string advertised)
	{
		// Stalwart 0.13 advertises absolute URLs on its own hostname; 0.16 advertises
		// relative paths. Both anchor onto the base URL's origin, keeping path + templates.
		string origin = $"{_baseUri.Scheme}://{_baseUri.Authority}";
		if (advertised.StartsWith('/'))
			return origin + advertised;
		int schemeEnd = advertised.IndexOf("://", StringComparison.Ordinal);
		if (schemeEnd < 0)
			return $"{origin}/{advertised}";
		int pathStart = advertised.IndexOf('/', schemeEnd + 3);
		return origin + (pathStart >= 0 ? advertised[pathStart..] : "/");
	}

	private static string RequireString(JsonElement root, string name)
	{
		return root.TryGetProperty(name, out JsonElement value) && value.GetString() is { Length: > 0 } s
			? s
			: throw new BackendException($"JMAP session resource is missing '{name}'.");
	}

	/// <summary>
	///   Every JMAP call funnels through here, so fast transient retry lives at this one seam.
	///   Reads (Foo/get, Foo/query, …) are replayed on a transient blip; writes are NOT — a
	///   replayed */set or Email/import would create a duplicate, since JMAP creationIds dedup only
	///   within a single request (see <see cref="AllReadOnly" />). The caller sets
	///   <paramref name="idempotent" /> accordingly.
	/// </summary>
	private Task<HttpResponseMessage> SendAsync(
		Func<HttpRequestMessage> createRequest, CancellationToken ct, bool idempotent = true)
	{
		return TransientRetry.SendHttpAsync(
			() => SendFollowingRedirectsAsync(createRequest, ct), ct, idempotent,
			onRetry: (reason, attempt) =>
			{
				GatewayMetrics.RecordBackendRetry("jmap");
				_wireLogger?.LogDebug("JMAP request transient failure ({Reason}); retry {Attempt}/{Max}",
					reason, attempt, TransientRetry.DelaysMs.Length);
			});
	}

	/// <summary>
	///   True only when every call is a side-effect-free read; any */set, */import, */copy, … can
	///   create or mutate server state and must never be replayed (a JMAP creationId such as "c" or
	///   "m" dedups only within one method call, so a replayed request creates a duplicate).
	/// </summary>
	private static bool AllReadOnly(IReadOnlyList<JmapCall> calls)
	{
		foreach (JmapCall call in calls)
		{
			int slash = call.Name.IndexOf('/');
			string verb = slash >= 0 ? call.Name[(slash + 1)..] : call.Name;
			if (verb is not ("get" or "query" or "changes" or "queryChanges"))
				return false;
		}

		return true;
	}

	/// <summary>
	///   Sends a request, following same-origin redirects manually with the method, body and
	///   Authorization header intact (auto-redirect would strip auth). Mirrors WebDavClient.
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
			Uri currentUri = request.RequestUri!;
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
			Uri target = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
			if (!IsSafeRedirect(_baseUri, target))
				return response;
			response.Dispose();
			redirectTarget = target;
		}
	}

	/// <summary>
	///   A redirect is followed (carrying the Basic Authorization header) only when it stays on
	///   the same origin — identical scheme, host and port. Any other target could hand the
	///   credentials to another service or downgrade https→http, so it is not followed.
	/// </summary>
	private static bool IsSafeRedirect(Uri baseUri, Uri target)
	{
		return string.Equals(target.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
		       string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
		       target.Port == baseUri.Port;
	}

	private static async Task EnsureSuccessAsync(
		HttpResponseMessage response, string method, string what, CancellationToken ct)
	{
		if (response.IsSuccessStatusCode)
			return;
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			// Authentication failure — distinct from unreachable so the auth path can answer false.
			throw new JmapAuthenticationException($"JMAP {method} {what}: {(int)response.StatusCode} (authentication).");
		GatewayMetrics.RecordBackendError("jmap");
		// Body omitted from the message — it may contain PII and this reaches the logs.
		_ = ct;
		throw new BackendException($"JMAP {method} {what} failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
	}
}
