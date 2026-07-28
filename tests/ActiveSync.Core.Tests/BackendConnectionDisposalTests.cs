using System.Xml.Linq;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   <see cref="BackendConnection.DisposeAsync" /> must be idempotent, must keep disposing the
///   remaining resources when one throws (and surface the failures as an <see cref="AggregateException" />),
///   and must dispose disposable content stores (which may themselves hold live connections).
/// </summary>
public class BackendConnectionDisposalTests
{
	[Fact]
	public async Task Dispose_ContinuesPastAThrowingResource_AndAggregates()
	{
		Tracker throwing = new(throwOnDispose: true);
		Tracker survivor = new();
		BackendConnection connection = new([], ownedResources: [throwing, survivor]);

		AggregateException ex =
			await Assert.ThrowsAsync<AggregateException>(async () => await connection.DisposeAsync());

		Assert.Single(ex.InnerExceptions);
		Assert.Equal(1, throwing.DisposeCount); // it was attempted
		Assert.Equal(1, survivor.DisposeCount);  // and the later resource still got disposed
	}

	[Fact]
	public async Task Dispose_IsIdempotent()
	{
		Tracker resource = new();
		BackendConnection connection = new([], ownedResources: [resource]);

		await connection.DisposeAsync();
		await connection.DisposeAsync();

		Assert.Equal(1, resource.DisposeCount);
	}

	// `_disposed` was a plain bool — a read-then-write with no atomicity, so two callers
	// racing DisposeAsync (a session-eviction sweep vs. a request completing, both plausible
	// call sites in BackendSessionFactory) could both observe "not yet disposed" and both go on
	// to dispose the owned resource. This is a genuine data race with no single deterministic
	// trigger; the trial count/thread count below are chosen to make it likely to surface under
	// real thread-pool contention rather than to prove it on every run.
	[Fact]
	public async Task Dispose_ConcurrentCalls_DisposeOwnedResourceAtMostOnce()
	{
		for (int trial = 0; trial < 200; trial++)
		{
			Tracker resource = new();
			BackendConnection connection = new([], ownedResources: [resource]);

			using SemaphoreSlim gate = new(0, int.MaxValue);
			Task[] racers = [.. Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
			{
				await gate.WaitAsync();
				await connection.DisposeAsync();
			}))];

			gate.Release(16);
			await Task.WhenAll(racers);

			Assert.Equal(1, resource.DisposeCount);
		}
	}

	[Fact]
	public async Task Dispose_DisposesDisposableStores()
	{
		DisposableStore store = new();
		BackendConnection connection = new([store]);

		await connection.DisposeAsync();

		Assert.Equal(1, store.DisposeCount);
	}

	private sealed class Tracker(bool throwOnDispose = false) : IAsyncDisposable
	{
		private int _disposeCount;

		// Interlocked so the COUNTER itself is never the source of a lost update — it must report
		// the true number of DisposeAsync calls even when BackendConnection lets more than one
		// through concurrently.
		public int DisposeCount => Volatile.Read(ref _disposeCount);

		public ValueTask DisposeAsync()
		{
			Interlocked.Increment(ref _disposeCount);
			if (throwOnDispose)
				throw new InvalidOperationException("boom");
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>A content store that owns a connection and therefore needs disposing.</summary>
	private sealed class DisposableStore : IContentStore, IAsyncDisposable
	{
		public int DisposeCount { get; private set; }

		public ValueTask DisposeAsync()
		{
			DisposeCount++;
			return ValueTask.CompletedTask;
		}

		public string EasClass => "Email";
		public bool OwnsBackendKey(string backendKey) => false;

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
			string folderBackendKey, ContentFilter filter, CancellationToken ct) => throw new NotSupportedException();

		public Task<BackendItem?> GetItemAsync(
			string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(string ItemKey, string Revision)> CreateItemAsync(
			string folderBackendKey, XElement applicationData, CancellationToken ct) => throw new NotSupportedException();

		public Task<string> UpdateItemAsync(
			string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteItemAsync(
			string folderBackendKey, string itemKey, bool permanent, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<string>> WaitForChangesAsync(
			IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
