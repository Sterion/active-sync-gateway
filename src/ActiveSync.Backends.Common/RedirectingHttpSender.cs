using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Common;

/// <summary>
///   Sends one logical HTTP request, following same-origin redirects manually with the method,
///   body and Authorization header intact. <see cref="HttpClient" />'s built-in auto-redirect
///   strips the Authorization header and downgrades non-GET methods on 301/302 — which turns a
///   well-known discovery redirect (e.g. Stalwart's 307 from <c>/.well-known/caldav</c> to the real
///   collection) into an unauthenticated request or an HTML login page. This is the shared home for
///   logic that used to be duplicated near-verbatim in <c>WebDavClient</c> and <c>JmapClient</c>
///   (S3) — the JMAP copy's own comment used to say "Mirrors WebDavClient". Both callers wrap the
///   returned task in <see cref="TransientRetry.SendHttpAsync" /> themselves; this type owns only
///   the redirect walk and the same-origin credential-forwarding guard.
/// </summary>
public sealed class RedirectingHttpSender(HttpClient http, Uri baseUri, ILogger? wireLogger = null)
{
	/// <summary>
	///   Sends a request built fresh by <paramref name="createRequest" /> (invoked once per hop,
	///   since <see cref="HttpRequestMessage" /> is single-use), following up to 5 same-origin
	///   redirects (relative to this sender's own base URI). An off-origin redirect (different
	///   scheme, host, or port) is not followed — the redirect response is returned as-is so the
	///   caller's own status handling surfaces it.
	/// </summary>
	public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
	{
		bool trace = wireLogger?.IsEnabled(LogLevel.Trace) == true;
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
				wireLogger!.LogTrace("{Method} {Uri} request: {Payload}",
					method, currentUri,
					request.Content is null
						? "(no body)"
						: WireLog.Payload(await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
			HttpResponseMessage response;
			try
			{
				response = await http.SendAsync(request, ct).ConfigureAwait(false);
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
					wireLogger!.LogTrace("{StatusCode} {Status} for {Method} {Uri}: {Payload}",
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
			if (!IsSafeRedirect(baseUri, target))
				return response;
			response.Dispose();
			redirectTarget = target;
		}
	}

	/// <summary>
	///   A redirect is followed (with the Authorization header attached) only when it stays on the
	///   same origin — identical scheme, host AND port. Any other target (a different port, or an
	///   https→http downgrade) could hand the credentials to another service or put them on the
	///   wire in cleartext, so the redirect is not followed.
	/// </summary>
	public static bool IsSafeRedirect(Uri baseUri, Uri target)
	{
		return string.Equals(target.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
		       string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
		       target.Port == baseUri.Port;
	}
}
