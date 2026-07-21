using System.Net;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The shared transient-retry helper: the idempotency gate, the caller's transient predicate, the
///   fixed backoff budget, HTTP status/exception classification, and response disposal on a
///   discarded 5xx.
/// </summary>
public class TransientRetryTests
{
	[Fact]
	public async Task RunAsync_SuccessOnFirstTry_RunsOnce()
	{
		int calls = 0;
		int result = await TransientRetry.RunAsync<int>(
			() =>
			{
				calls++;
				return Task.FromResult(42);
			}, _ => true, CancellationToken.None);

		Assert.Equal(42, result);
		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task RunAsync_TransientThenSuccess_Retries()
	{
		int calls = 0;
		int result = await TransientRetry.RunAsync<int>(
			() =>
			{
				calls++;
				if (calls == 1)
					throw new IOException("blip");
				return Task.FromResult(7);
			}, ex => ex is IOException, CancellationToken.None);

		Assert.Equal(7, result);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task RunAsync_NonTransient_PropagatesWithoutRetry()
	{
		int calls = 0;
		await Assert.ThrowsAsync<InvalidOperationException>(() => TransientRetry.RunAsync<int>(
			() =>
			{
				calls++;
				throw new InvalidOperationException();
			}, ex => ex is IOException, CancellationToken.None));

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task RunAsync_NotIdempotent_RunsOnceEvenWhenTransient()
	{
		int calls = 0;
		await Assert.ThrowsAsync<IOException>(() => TransientRetry.RunAsync<int>(
			() =>
			{
				calls++;
				throw new IOException("blip");
			}, _ => true, CancellationToken.None, idempotent: false));

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task RunAsync_BudgetExhausted_PropagatesLastError()
	{
		int calls = 0;
		await Assert.ThrowsAsync<IOException>(() => TransientRetry.RunAsync<int>(
			() =>
			{
				calls++;
				throw new IOException("always");
			}, _ => true, CancellationToken.None));

		// The initial attempt plus one retry per backoff step.
		Assert.Equal(TransientRetry.DelaysMs.Length + 1, calls);
	}

	[Fact]
	public async Task RunAsync_CancelDuringBackoff_ThrowsOperationCanceled()
	{
		using CancellationTokenSource cts = new();
		await cts.CancelAsync(); // already cancelled, so the first inter-try backoff throws
		int calls = 0;
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TransientRetry.RunAsync<int>(
			() =>
			{
				calls++;
				throw new IOException("blip");
			}, _ => true, cts.Token));

		Assert.Equal(1, calls); // ran once, then the cancelled backoff aborted the retry
	}

	[Fact]
	public async Task SendHttpAsync_TransientStatusThenSuccess_RetriesAndDisposesDiscarded()
	{
		List<TrackingResponse> issued = new();
		int calls = 0;
		HttpResponseMessage final = await TransientRetry.SendHttpAsync(() =>
		{
			calls++;
			TrackingResponse response = new(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
			issued.Add(response);
			return Task.FromResult<HttpResponseMessage>(response);
		}, CancellationToken.None);

		Assert.Equal(HttpStatusCode.OK, final.StatusCode);
		Assert.Equal(2, calls);
		Assert.True(issued[0].Disposed);  // the discarded 503 was disposed before the retry
		Assert.False(issued[1].Disposed); // the returned 200 is still live
		final.Dispose();
	}

	[Fact]
	public async Task SendHttpAsync_NotIdempotent_ReturnsTransientStatusUnretried()
	{
		int calls = 0;
		using HttpResponseMessage response = await TransientRetry.SendHttpAsync(() =>
		{
			calls++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
		}, CancellationToken.None, idempotent: false);

		Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
		Assert.Equal(1, calls);
	}

	[Fact]
	public void IsTransientHttpException_TrueForTransport_FalseWhenCallerCancelled()
	{
		Assert.True(TransientRetry.IsTransientHttpException(new HttpRequestException(), CancellationToken.None));
		Assert.True(TransientRetry.IsTransientHttpException(new IOException(), CancellationToken.None));
		Assert.False(TransientRetry.IsTransientHttpException(new InvalidOperationException(), CancellationToken.None));

		using CancellationTokenSource cts = new();
		cts.Cancel();
		Assert.False(TransientRetry.IsTransientHttpException(new HttpRequestException(), cts.Token));
	}

	[Theory]
	[InlineData(500, true)]
	[InlineData(502, true)]
	[InlineData(503, true)]
	[InlineData(504, true)]
	[InlineData(400, false)]
	[InlineData(404, false)]
	[InlineData(200, false)]
	public void IsTransientStatus_OnlyReplayable5xx(int code, bool expected)
	{
		Assert.Equal(expected, TransientRetry.IsTransientStatus((HttpStatusCode)code));
	}

	private sealed class TrackingResponse(HttpStatusCode status) : HttpResponseMessage(status)
	{
		public bool Disposed { get; private set; }

		protected override void Dispose(bool disposing)
		{
			Disposed = true;
			base.Dispose(disposing);
		}
	}
}
