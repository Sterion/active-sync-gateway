using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The provider engine: registry name/role validation and the composite session's
///   grouping, store dispatch, and mandatory-mail-role enforcement.
/// </summary>
public class BackendProviderTests
{
	private static readonly BackendCredentials Gateway = new() { UserName = "user@x", Password = "pw" };

	private static BackendProviderRegistry Registry(params IBackendProvider[] providers)
	{
		return new BackendProviderRegistry(providers, NullLogger<BackendProviderRegistry>.Instance);
	}

	[Fact]
	public void Registry_RejectsDuplicateNames_AndUnknownLookups()
	{
		FakeProvider a = new("mail", [BackendRole.MailStore, BackendRole.MailSubmit]);
		Assert.Throws<InvalidOperationException>(() =>
			Registry(a, new FakeProvider("MAIL", [BackendRole.Calendar])));

		BackendProviderRegistry registry = Registry(a);
		Assert.Same(a, registry.GetFor("mail", BackendRole.MailStore));
		Assert.Same(a, registry.GetFor("MAIL", BackendRole.MailStore)); // case-insensitive
		InvalidOperationException unknown = Assert.Throws<InvalidOperationException>(() =>
			registry.GetFor("jmap", BackendRole.MailStore));
		Assert.Contains("mail", unknown.Message); // names the available providers
		Assert.Throws<InvalidOperationException>(() => registry.GetFor("mail", BackendRole.Notes));
	}

	[Fact]
	public async Task Session_GroupsRolesByProvider_AndAggregatesStores()
	{
		FakeProvider mail = new("mail", [BackendRole.MailStore, BackendRole.MailSubmit]);
		FakeProvider rest = new("rest", [BackendRole.Calendar, BackendRole.Contacts]);
		CompositeBackendSession session = await CompositeBackendSession.CreateAsync(Registry(mail, rest), Gateway, 1, "user@x",
			[
				new ResolvedRole { Role = BackendRole.MailStore, ProviderName = "mail", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.MailSubmit, ProviderName = "mail", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.Calendar, ProviderName = "rest", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.Contacts, ProviderName = "rest", Settings = ProviderSettings.Empty, Credentials = Gateway }
			], [], CancellationToken.None);

		// One connection per provider, carrying exactly the roles assigned to it.
		Assert.Equal(1, mail.Connections);
		Assert.Equal(1, rest.Connections);
		Assert.Equal([BackendRole.MailStore, BackendRole.MailSubmit], mail.LastAssignedRoles);
		Assert.Equal([BackendRole.Calendar, BackendRole.Contacts], rest.LastAssignedRoles);

		Assert.Equal(3, session.Stores.Count); // MailSubmit contributes no store
		Assert.NotNull(session.Mail);
		Assert.NotNull(session.MailSubmit);
		// The store's class is DERIVED from its alias interface, never declared.
		Assert.Equal("Calendar", session.EasClassOf(session.GetStoreForClass("Calendar")!));

		// Key dispatch goes through OwnsKey; read-only routes to the owning store.
		Assert.Equal("Email", session.EasClassOf(session.GetStoreForKey(new FolderKey("mail-MailStore:INBOX"))!));
		Assert.Null(session.GetStoreForKey(new FolderKey("jmap:INBOX")));
		Assert.True(session.IsReadOnlyFolder(new FolderKey("rest-Calendar:shared")));
		Assert.False(session.IsReadOnlyFolder(new FolderKey("rest-Calendar:own")));
	}

	[Fact]
	public async Task Session_RequiresBothMailRoles()
	{
		FakeProvider store = new("store", [BackendRole.MailStore]);
		FakeProvider submit = new("submit", [BackendRole.MailSubmit]);
		await Assert.ThrowsAsync<InvalidOperationException>(() => CompositeBackendSession.CreateAsync(
			Registry(store, submit), Gateway, 1, null,
			[new ResolvedRole { Role = BackendRole.MailStore, ProviderName = "store", Settings = ProviderSettings.Empty, Credentials = Gateway }], [], CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => CompositeBackendSession.CreateAsync(
			Registry(store, submit), Gateway, 1, null,
			[new ResolvedRole { Role = BackendRole.MailSubmit, ProviderName = "submit", Settings = ProviderSettings.Empty, Credentials = Gateway }], [], CancellationToken.None));
	}

	[Fact]
	public async Task CreateAsync_DisposesAlreadyOpenedConnections_WhenALaterProviderFails()
	{
		// The provider loop in CreateAsync has no try/catch — when a LATER provider's
		// CreateConnectionAsync throws (a bad BaseUrl, an unsupported role, a transport-open
		// failure), the half-built session is never returned and nothing disposes the EARLIER
		// provider's already-open connection. One request against a half-broken multi-provider
		// configuration leaks every connection opened before the failing one.
		FakeProvider good = new("good", [BackendRole.MailStore, BackendRole.MailSubmit]);
		FakeProvider bad = new("bad", [BackendRole.Calendar], throwOnCreate: true);

		await Assert.ThrowsAsync<InvalidOperationException>(() => CompositeBackendSession.CreateAsync(
			Registry(good, bad), Gateway, 1, null,
			[
				new ResolvedRole { Role = BackendRole.MailStore, ProviderName = "good", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.MailSubmit, ProviderName = "good", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.Calendar, ProviderName = "bad", Settings = ProviderSettings.Empty, Credentials = Gateway }
			], [], CancellationToken.None));

		Assert.True(good.LastResource!.Disposed); // the earlier connection must not leak
	}

	[Fact]
	public async Task Session_Dispose_DisposesEveryConnectionResource()
	{
		FakeProvider mail = new("mail", [BackendRole.MailStore, BackendRole.MailSubmit]);
		CompositeBackendSession session = await CompositeBackendSession.CreateAsync(Registry(mail), Gateway, 1, null,
			[
				new ResolvedRole { Role = BackendRole.MailStore, ProviderName = "mail", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.MailSubmit, ProviderName = "mail", Settings = ProviderSettings.Empty, Credentials = Gateway }
			], [], CancellationToken.None);
		await session.DisposeAsync();
		Assert.True(mail.LastResource!.Disposed);
	}

	[Fact]
	public async Task Session_Dispose_ContinuesPastAThrowingConnection_WithoutThrowing()
	{
		// A provider whose connection throws on dispose (e.g. IMAP LOGOUT on a
		// dead socket) must not strand the other providers' connections — they still hold live
		// sockets.
		//
		// Behaviour change: DisposeAsync used to rethrow the collected failures as an
		// AggregateException. EasEndpoint's `await using session = ...` sits OUTSIDE its
		// try/catch, so that escaped as an unhandled exception on a request whose response had
		// ALREADY been written successfully — for a lease release that has nothing to do with the
		// request's own outcome (idle eviction, a live settings recycle, password rotation).
		// Disposal failures are now logged, never rethrown.
		FakeProvider bad = new("bad", [BackendRole.MailStore, BackendRole.MailSubmit], throwOnDispose: true);
		FakeProvider good = new("good", [BackendRole.Calendar]);
		CompositeBackendSession session = await CompositeBackendSession.CreateAsync(Registry(bad, good), Gateway, 1, null,
			[
				new ResolvedRole { Role = BackendRole.MailStore, ProviderName = "bad", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.MailSubmit, ProviderName = "bad", Settings = ProviderSettings.Empty, Credentials = Gateway },
				new ResolvedRole { Role = BackendRole.Calendar, ProviderName = "good", Settings = ProviderSettings.Empty, Credentials = Gateway }
			], [], CancellationToken.None);

		Exception? escaped = await Record.ExceptionAsync(async () => await session.DisposeAsync());

		Assert.Null(escaped);             // must never throw into the caller's `await using`
		Assert.True(bad.LastResource!.Disposed);  // the throwing connection was still attempted
		Assert.True(good.LastResource!.Disposed); // and the later connection still got disposed
	}

	private sealed class FakeProvider(string name, BackendRole[] roles, bool throwOnDispose = false, bool throwOnCreate = false)
		: IBackendProvider
	{
		public int Connections { get; private set; }
		public IReadOnlyList<BackendRole>? LastAssignedRoles { get; private set; }
		public FakeResource? LastResource { get; private set; }

		public string Name => name;
		public IReadOnlySet<BackendRole> SupportedRoles { get; } = new HashSet<BackendRole>(roles);

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
		{
		}

		public string DescribeRole(BackendRole role, ProviderSettings settings) => $"{name} fake";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
		{
			if (throwOnCreate)
				throw new InvalidOperationException($"{name}: simulated transport-open failure");
			Connections++;
			LastAssignedRoles = context.Roles.Select(r => r.Role).ToList();
			LastResource = new FakeResource(throwOnDispose);
			List<IContentStore> stores = context.Roles
				.Where(r => r.Role is not (BackendRole.MailSubmit or BackendRole.Oof))
				.Select(IContentStore (r) => r.Role switch
				{
					BackendRole.MailStore => new FakeMailStore($"{name}-{r.Role}"),
					BackendRole.Calendar => new FakeCalendarStore($"{name}-{r.Role}"),
					BackendRole.Contacts => new FakeContactStore($"{name}-{r.Role}"),
					_ => throw new NotSupportedException($"No stub store for the {r.Role} role.")
				})
				.ToList();
			return Task.FromResult<IBackendConnection>(new BackendConnection(
				stores,
				context.Roles.Any(r => r.Role == BackendRole.MailSubmit) ? new FakeSubmit() : null,
				ownedResources: [OwnedResource.OfAsync(LastResource)]));
		}
	}

	private sealed class FakeResource(bool throwOnDispose = false) : IAsyncDisposable
	{
		public bool Disposed { get; private set; }

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			if (throwOnDispose)
				throw new InvalidOperationException("connection dispose failed");
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeSubmit : IMailSubmitOperations
	{
		public Task SendAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct) => Task.CompletedTask;
	}

	/// <summary>
	///   The class-agnostic half of a stub store with a "{prefix}:" key space. A store's content
	///   class is DERIVED from which alias interface it implements, so each class gets its own
	///   subclass rather than one type claiming several.
	/// </summary>
	private abstract class FakeStore(string prefix) : IContentStore
	{
		public bool OwnsKey(FolderKey key) =>
			key.Value.StartsWith(prefix + ":", StringComparison.Ordinal);

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
			FolderKey folder, ContentFilter filter, CancellationToken ct) => throw new NotSupportedException();

		public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct) =>
			throw new NotSupportedException();

		// Item move and folder mutation are optional capabilities; these stubs implement neither.

		public Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
			IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	/// <summary>The mail stub: the mailbox side-operations are mandatory alongside the mail store.</summary>
	private sealed class FakeMailStore(string prefix) : FakeStore(prefix), IMailStore, IMailboxOperations
	{
		public Task<MailItem?> GetItemAsync(
			FolderKey folder, ItemKey item, MailFetchOptions options, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
			FolderKey folder, MailItem item, CancellationToken ct) => throw new NotSupportedException();

		public Task<ItemRevision> UpdateFlagsAsync(
			FolderKey folder, ItemKey item, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
			FolderKey folder, ItemKey item, MailItem value, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(FolderKey folder, ItemKey item, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SetAnsweredAsync(FolderKey folder, ItemKey item, bool forwarded, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<SearchHit>> SearchAsync(
			FolderKey? folder, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task EmptyFolderAsync(FolderKey folder, CancellationToken ct) => throw new NotSupportedException();
	}

	/// <summary>The calendar stub: also the read-only-grant source the dispatch test exercises.</summary>
	private sealed class FakeCalendarStore(string prefix)
		: FakeStore(prefix), ICalendarStore, IMeetingOperations, IReadOnlyCollectionSource
	{
		public bool IsReadOnlyCollection(FolderKey folder) => folder.Value.EndsWith(":shared");

		public Task<CalendarItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
			FolderKey folder, CalendarItem item, CancellationToken ct) => throw new NotSupportedException();

		public Task<ItemRevision> UpdateItemAsync(
			FolderKey folder, ItemKey item, CalendarItem value, ItemRevision? expected, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<ItemKey?> RespondToMeetingAsync(
			FolderKey calendar, string eventUid, MeetingResponseKind response, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct) => throw new NotSupportedException();
	}

	/// <summary>The contacts stub.</summary>
	private sealed class FakeContactStore(string prefix) : FakeStore(prefix), IContactStore
	{
		public Task<ContactItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
			FolderKey folder, ContactItem item, CancellationToken ct) => throw new NotSupportedException();

		public Task<ItemRevision> UpdateItemAsync(
			FolderKey folder, ItemKey item, ContactItem value, ItemRevision? expected, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
