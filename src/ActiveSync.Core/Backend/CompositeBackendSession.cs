using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Composite backend session: groups the account's resolved roles by provider, opens one
///   connection per provider, and aggregates the resulting stores and side operations. The
///   session itself has no protocol knowledge — every backend behind it is a provider.
/// </summary>
public sealed class CompositeBackendSession : IBackendSession
{
	private readonly List<IBackendConnection> _connections = [];
	private readonly List<IContentStore> _stores = [];
	// Store → derived EAS class, built once at session build (the derivation also validates the
	// exactly-one-alias contract rule per store, so a misbuilt provider fails at connection
	// creation rather than mid-sync).
	private readonly Dictionary<IContentStore, string> _storeClasses = new(ReferenceEqualityComparer.Instance);
	// Only used so a disposal failure can be logged instead of thrown into the caller's
	// `await using` — optional because most callers (tests, in particular) have no logger handy
	// and a swallow-silently fallback is still strictly better than the previous throw.
	private readonly ILogger? _logger;

	// The session is a refcounted lease. The cache owns the initial reference; every
	// GetSessionAsync hands out an additional lease that the request releases (via DisposeAsync)
	// when it finishes. The connections are torn down only when the last lease is released — so
	// the idle sweep evicting a session a long-running Ping is still using drops the cache's
	// reference but leaves the live socket intact until the request lets go.
	private int _leaseCount = 1;

	private CompositeBackendSession(BackendCredentials gatewayCredentials, int userId, string? mailAddress, ILogger? logger)
	{
		// The gateway UserId is the IDENTITY (DB scoping, encryption AAD, durable keys); the
		// credentials carry the presented login. Each backend authenticates with its role's
		// resolved credentials.
		Credentials = gatewayCredentials;
		UserId = userId;
		MailAddress = mailAddress;
		_logger = logger;
	}

	/// <summary>
	///   Opens one connection per provider (<see cref="IBackendProvider.CreateConnectionAsync" />
	///   is async — the composite awaits each provider's transport open) and aggregates the resulting
	///   stores and side operations.
	/// </summary>
	public static async Task<CompositeBackendSession> CreateAsync(
		BackendProviderRegistry registry,
		BackendCredentials gatewayCredentials,
		int userId,
		string? mailAddress,
		IReadOnlyList<ResolvedRole> roles,
		IReadOnlyList<SharedCollection> sharedCollections,
		CancellationToken ct,
		ILogger? logger = null)
	{
		CompositeBackendSession session = new(gatewayCredentials, userId, mailAddress, logger);

		// Everything below can throw AFTER one or more providers already opened a live
		// connection (a later provider's bad BaseUrl, an unsupported role, a transport-open
		// failure, or either `?? throw` below). Without this guard the half-built session is
		// discarded — never returned, so nothing ever disposes the connections already gathered
		// in `session._connections` — and a phone Pinging against a half-broken configuration
		// leaks one provider's sockets per attempt. Dispose whatever was opened so far before
		// letting the failure propagate; DisposeConnectionsAsync never throws, so this
		// cleanup cannot mask the original exception.
		try
		{
			IMailSubmitOperations? mailSubmit = null;
			IOofBackend? oof = null;
			foreach (IGrouping<string, ResolvedRole> group in
				roles.GroupBy(r => r.ProviderName, StringComparer.OrdinalIgnoreCase))
			{
				IBackendProvider provider = registry.GetFor(group.Key, group.First().Role);
				List<ResolvedRole> assigned = group.ToList();
				foreach (ResolvedRole role in assigned)
					registry.GetFor(group.Key, role.Role); // validates every assigned role
				IBackendConnection connection = await provider.CreateConnectionAsync(
					new BackendConnectionContext
					{
						GatewayCredentials = gatewayCredentials,
						GatewayUserId = userId,
						MailAddress = mailAddress,
						Roles = assigned,
						SharedCollections = sharedCollections
					}, ct)
					.ConfigureAwait(false);
				session._connections.Add(connection);
				foreach (IContentStore store in connection.Stores)
				{
					// EasClassOf also enforces the exactly-one-alias rule per store.
					session._storeClasses[store] = ContentStoreClasses.EasClassOf(store);
					session._stores.Add(store);
				}

				mailSubmit ??= connection.MailSubmit;
				oof ??= connection.Oof;
			}

			session.Mail = session.GetStoreForClass(EasClass.Email) as IMailStore
				?? throw new InvalidOperationException("No provider filled the MailStore role for this session.");
			// The mailbox side operations are mandatory alongside the mail store: SmartReply,
			// Search, EmptyFolderContents and host-side attachment extraction all need them.
			session.Mailbox = session.Mail as IMailboxOperations
				?? throw new InvalidOperationException(
					"The MailStore role's store does not implement IMailboxOperations, which the gateway requires.");
			session.MailSubmit = mailSubmit
				?? throw new InvalidOperationException("No provider filled the MailSubmit role for this session.");
			session.Calendar = session.GetStoreForClass(EasClass.Calendar) as IMeetingOperations;
			session.Contacts = session.GetStoreForClass(EasClass.Contacts) as IDirectoryOperations;
			session.Oof = oof;
			return session;
		}
		catch
		{
			await session.DisposeConnectionsAsync().ConfigureAwait(false);
			throw;
		}
	}

	// Written on the request path and read by the eviction timer thread — a bare DateTime is
	// larger than a word and has no read/write atomicity guarantee, so the timer could read a torn
	// or indefinitely-stale value. Backed by long ticks with Interlocked read/write.
	private long _lastUsedTicks = DateTime.UtcNow.Ticks;

	internal DateTime LastUsedUtc
	{
		get => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
		set => Interlocked.Exchange(ref _lastUsedTicks, value.Ticks);
	}

	public BackendCredentials Credentials { get; }
	public int UserId { get; }
	public string? MailAddress { get; }
	public IReadOnlyList<IContentStore> Stores => _stores;
	public IMailStore Mail { get; private set; } = null!;
	public IMailboxOperations Mailbox { get; private set; } = null!;
	public IMailSubmitOperations MailSubmit { get; private set; } = null!;
	public IDirectoryOperations? Contacts { get; private set; }
	public IMeetingOperations? Calendar { get; private set; }
	public IOofBackend? Oof { get; private set; }
	public SessionPayloadCache PayloadCache { get; } = new();

	public IContentStore? GetStoreForClass(string easClass)
	{
		return _stores.FirstOrDefault(s =>
			_storeClasses[s].Equals(easClass, StringComparison.OrdinalIgnoreCase));
	}

	public IContentStore? GetStoreForKey(FolderKey key)
	{
		return _stores.FirstOrDefault(s => s.OwnsKey(key));
	}

	public string EasClassOf(IContentStore store)
	{
		return _storeClasses.TryGetValue(store, out string? easClass)
			? easClass
			: ContentStoreClasses.EasClassOf(store);
	}

	public bool IsReadOnlyFolder(FolderKey folder)
	{
		return GetStoreForKey(folder) is IReadOnlyCollectionSource source &&
		       source.IsReadOnlyCollection(folder);
	}

	/// <summary>
	///   Acquires a lease on this session, or returns false if it is already being torn down (the
	///   last lease was released between a caller reading it from the cache and getting here). A
	///   false return tells the caller to drop the stale entry and rebuild.
	/// </summary>
	internal bool TryAcquireLease()
	{
		int current = Volatile.Read(ref _leaseCount);
		while (current > 0)
		{
			int prev = Interlocked.CompareExchange(ref _leaseCount, current + 1, current);
			if (prev == current)
				return true;
			current = prev;
		}

		return false;
	}

	/// <summary>Releases one lease. The connections are disposed only on the last release.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Decrement(ref _leaseCount) != 0)
			return;
		await DisposeConnectionsAsync().ConfigureAwait(false);
	}

	private async ValueTask DisposeConnectionsAsync()
	{
		// One throwing connection (e.g. an IMAP LOGOUT on a dead socket) must not abort the
		// loop and strand the remaining connections' live sockets — dispose them all, then surface
		// the failures together.
		List<Exception>? failures = null;
		foreach (IBackendConnection connection in _connections)
			try
			{
				await connection.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				(failures ??= []).Add(ex);
			}

		// This used to rethrow, but the public DisposeAsync() above is reached through the
		// request's `await using` in EasEndpoint — which sits OUTSIDE its try/catch — so a
		// throwing teardown (e.g. an IMAP LOGOUT on a dead socket) surfaced as an unhandled
		// exception for a lease release that has nothing to do with the request's own outcome
		// (the response may already be written). It also reaches here from CreateAsync's failure
		// cleanup, where throwing would replace the ORIGINAL build failure. Log instead —
		// every connection was still given its chance to dispose above.
		if (failures is { Count: > 0 })
			_logger?.LogWarning(new AggregateException(failures),
				"One or more backend connections failed to dispose for user {UserId}", UserId);
	}
}
