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

	internal DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

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

	public async ValueTask DisposeAsync()
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
