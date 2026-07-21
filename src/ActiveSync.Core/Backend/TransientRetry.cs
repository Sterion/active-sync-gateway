using System.Net;

using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Bounded, allocation-light transient retry for backend operations. No external resilience
///   dependency: a fixed short backoff, a caller-supplied "is this worth another try" predicate,
///   and an idempotency gate so a non-replayable operation (an IMAP APPEND, the SMTP DATA phase,
///   a JMAP create) is never run twice. Retries are FAST (a couple hundred ms) — the intent is to
///   ride out a momentary blip under load, not to wait out an outage.
/// </summary>
public static class TransientRetry
{
	/// <summary>Backoff before each retry; its length is the retry budget (so 3 attempts total).</summary>
	public static readonly int[] DelaysMs = [150, 400];

	/// <summary>
	///   Runs <paramref name="action" />, replaying it on a transient failure up to the
	///   <see cref="DelaysMs" /> budget. When <paramref name="idempotent" /> is false the action
	///   runs exactly once (the predicate is never consulted) — the caller has judged a replay
	///   unsafe. <paramref name="onRetry" /> is invoked with the failing exception and the 1-based
	///   retry number just before each backoff.
	/// </summary>
	public static async Task<T> RunAsync<T>(
		Func<Task<T>> action,
		Func<Exception, bool> isTransient,
		CancellationToken ct,
		bool idempotent = true,
		Action<Exception, int>? onRetry = null)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				return await action().ConfigureAwait(false);
			}
			catch (Exception ex) when (idempotent && attempt < DelaysMs.Length && isTransient(ex))
			{
				onRetry?.Invoke(ex, attempt + 1);
				await Task.Delay(DelaysMs[attempt], ct).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Void-returning overload for actions with no result.</summary>
	public static Task RunAsync(
		Func<Task> action,
		Func<Exception, bool> isTransient,
		CancellationToken ct,
		bool idempotent = true,
		Action<Exception, int>? onRetry = null)
	{
		return RunAsync(async () =>
		{
			await action().ConfigureAwait(false);
			return true;
		}, isTransient, ct, idempotent, onRetry);
	}

	/// <summary>
	///   HTTP specialisation shared by the DAV and JMAP clients: retries on a transient transport
	///   failure OR a replayable 5xx, disposing each discarded response first. <paramref name="send" />
	///   is the underlying (redirect-following) send; <paramref name="onRetry" /> gets a short reason
	///   token (exception type name or status code) and the 1-based retry number.
	/// </summary>
	public static async Task<HttpResponseMessage> SendHttpAsync(
		Func<Task<HttpResponseMessage>> send,
		CancellationToken ct,
		bool idempotent = true,
		Action<string, int>? onRetry = null)
	{
		for (int attempt = 0; ; attempt++)
		{
			bool canRetry = idempotent && attempt < DelaysMs.Length;
			HttpResponseMessage response;
			try
			{
				response = await send().ConfigureAwait(false);
			}
			catch (Exception ex) when (canRetry && IsTransientHttpException(ex, ct))
			{
				onRetry?.Invoke(ex.GetType().Name, attempt + 1);
				await Task.Delay(DelaysMs[attempt], ct).ConfigureAwait(false);
				continue;
			}

			if (canRetry && IsTransientStatus(response.StatusCode))
			{
				onRetry?.Invoke(((int)response.StatusCode).ToString(), attempt + 1);
				response.Dispose();
				await Task.Delay(DelaysMs[attempt], ct).ConfigureAwait(false);
				continue;
			}

			return response;
		}
	}

	/// <summary>
	///   A transport-level HTTP failure worth replaying — a dropped/refused connection or a
	///   library timeout (the internal-timeout <see cref="TaskCanceledException" />). Never true
	///   when the caller's own token was cancelled (the client went away — do not retry).
	/// </summary>
	public static bool IsTransientHttpException(Exception ex, CancellationToken ct)
	{
		return !ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException;
	}

	/// <summary>The replayable server-side statuses: 500/502/503/504 (transient upstream failures).</summary>
	public static bool IsTransientStatus(HttpStatusCode code)
	{
		return (int)code is 500 or 502 or 503 or 504;
	}
}
