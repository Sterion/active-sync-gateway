using ActiveSync.Protocol;

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

	// A2: the session is a refcounted lease. The cache owns the initial reference; every
	// GetSessionAsync hands out an additional lease that the request releases (via DisposeAsync)
	// when it finishes. The connections are torn down only when the last lease is released — so
	// the idle sweep evicting a session a long-running Ping is still using drops the cache's
	// reference but leaves the live socket intact until the request lets go.
	private int _leaseCount = 1;

	private CompositeBackendSession(BackendCredentials gatewayCredentials, string? mailAddress)
	{
		// The gateway credentials are the IDENTITY (DB scoping, encryption AAD, cache keys);
		// each backend authenticates with its role's resolved credentials.
		Credentials = gatewayCredentials;
		MailAddress = mailAddress;
	}

	/// <summary>
	///   Opens one connection per provider (K61: <see cref="IBackendProvider.CreateConnectionAsync" />
	///   is async — the composite awaits each provider's transport open) and aggregates the resulting
	///   stores and side operations.
	/// </summary>
	public static async Task<CompositeBackendSession> CreateAsync(
		BackendProviderRegistry registry,
		BackendCredentials gatewayCredentials,
		string? mailAddress,
		IReadOnlyList<ResolvedRole> roles,
		IReadOnlyList<SharedCollection> sharedCollections,
		CancellationToken ct)
	{
		CompositeBackendSession session = new(gatewayCredentials, mailAddress);

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
				new BackendConnectionContext(gatewayCredentials, mailAddress, assigned, sharedCollections), ct)
				.ConfigureAwait(false);
			session._connections.Add(connection);
			session._stores.AddRange(connection.Stores);
			mailSubmit ??= connection.MailSubmit;
			oof ??= connection.Oof;
		}

		session.MailStore = session.GetStoreForClass(EasClass.Email) as IMailStoreOperations
			?? throw new InvalidOperationException("No provider filled the MailStore role for this session.");
		session.MailSubmit = mailSubmit
			?? throw new InvalidOperationException("No provider filled the MailSubmit role for this session.");
		session.Calendar = session.GetStoreForClass(EasClass.Calendar) as ICalendarOperations;
		session.Contacts = session.GetStoreForClass(EasClass.Contacts) as IContactOperations;
		session.Oof = oof;
		return session;
	}

	// A24: written on the request path and read by the eviction timer thread — a bare DateTime is
	// larger than a word and has no read/write atomicity guarantee, so the timer could read a torn
	// or indefinitely-stale value. Backed by long ticks with Interlocked read/write.
	private long _lastUsedTicks = DateTime.UtcNow.Ticks;

	internal DateTime LastUsedUtc
	{
		get => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
		set => Interlocked.Exchange(ref _lastUsedTicks, value.Ticks);
	}

	public BackendCredentials Credentials { get; }
	public string? MailAddress { get; }
	public IReadOnlyList<IContentStore> Stores => _stores;
	public IMailStoreOperations MailStore { get; private set; } = null!;
	public IMailSubmitOperations MailSubmit { get; private set; } = null!;
	public IContactOperations? Contacts { get; private set; }
	public ICalendarOperations? Calendar { get; private set; }
	public IOofBackend? Oof { get; private set; }

	public IContentStore? GetStoreForClass(string easClass)
	{
		return _stores.FirstOrDefault(s => s.EasClass.Equals(easClass, StringComparison.OrdinalIgnoreCase));
	}

	public IContentStore? GetStoreForBackendKey(string backendKey)
	{
		return _stores.FirstOrDefault(s => s.OwnsBackendKey(backendKey));
	}

	public bool IsReadOnlyFolder(string folderBackendKey)
	{
		return GetStoreForBackendKey(folderBackendKey) is IReadOnlyCollectionSource source &&
		       source.IsReadOnlyCollection(folderBackendKey);
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

	/// <summary>Releases one lease (A2). The connections are disposed only on the last release.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Decrement(ref _leaseCount) != 0)
			return;
		await DisposeConnectionsAsync().ConfigureAwait(false);
	}

	private async ValueTask DisposeConnectionsAsync()
	{
		// A12: one throwing connection (e.g. an IMAP LOGOUT on a dead socket) must not abort the
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

		if (failures is { Count: > 0 })
			throw new AggregateException("One or more backend connections failed to dispose.", failures);
	}
}
