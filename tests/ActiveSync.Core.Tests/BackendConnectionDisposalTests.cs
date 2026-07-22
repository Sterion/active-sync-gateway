using System.Xml.Linq;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   K60: <see cref="BackendConnection.DisposeAsync" /> must be idempotent, must keep disposing the
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
		public int DisposeCount { get; private set; }

		public ValueTask DisposeAsync()
		{
			DisposeCount++;
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
