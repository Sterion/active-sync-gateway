using ActiveSync.Protocol;

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

	public CompositeBackendSession(
		BackendProviderRegistry registry,
		BackendCredentials gatewayCredentials,
		string? mailAddress,
		IReadOnlyList<ResolvedRole> roles,
		IReadOnlyList<SharedCollection> sharedCollections)
	{
		// The gateway credentials are the IDENTITY (DB scoping, encryption AAD, cache keys);
		// each backend authenticates with its role's resolved credentials.
		Credentials = gatewayCredentials;
		MailAddress = mailAddress;

		IMailSubmitOperations? mailSubmit = null;
		IOofBackend? oof = null;
		foreach (IGrouping<string, ResolvedRole> group in
			roles.GroupBy(r => r.ProviderName, StringComparer.OrdinalIgnoreCase))
		{
			IBackendProvider provider = registry.GetFor(group.Key, group.First().Role);
			List<ResolvedRole> assigned = group.ToList();
			foreach (ResolvedRole role in assigned)
				registry.GetFor(group.Key, role.Role); // validates every assigned role
			IBackendConnection connection = provider.CreateConnection(new BackendConnectionContext(
				gatewayCredentials, mailAddress, assigned, sharedCollections));
			_connections.Add(connection);
			_stores.AddRange(connection.Stores);
			mailSubmit ??= connection.MailSubmit;
			oof ??= connection.Oof;
		}

		MailStore = GetStoreForClass(EasClass.Email) as IMailStoreOperations
			?? throw new InvalidOperationException("No provider filled the MailStore role for this session.");
		MailSubmit = mailSubmit
			?? throw new InvalidOperationException("No provider filled the MailSubmit role for this session.");
		Calendar = GetStoreForClass(EasClass.Calendar) as ICalendarOperations;
		Contacts = GetStoreForClass(EasClass.Contacts) as IContactOperations;
		Oof = oof;
	}

	internal DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

	public BackendCredentials Credentials { get; }
	public string? MailAddress { get; }
	public IReadOnlyList<IContentStore> Stores => _stores;
	public IMailStoreOperations MailStore { get; }
	public IMailSubmitOperations MailSubmit { get; }
	public IContactOperations? Contacts { get; }
	public ICalendarOperations? Calendar { get; }
	public IOofBackend? Oof { get; }

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
		foreach (IBackendConnection connection in _connections)
			await connection.DisposeAsync().ConfigureAwait(false);
	}
}
