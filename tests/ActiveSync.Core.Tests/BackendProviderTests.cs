using System.Xml.Linq;
using ActiveSync.Backends;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The provider engine: registry name/role validation and the composite session's
///   grouping, store dispatch, and mandatory-mail-role enforcement.
/// </summary>
public class BackendProviderTests
{
	private static readonly BackendCredentials Gateway = new("user@x", "pw");

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
	public void Session_GroupsRolesByProvider_AndAggregatesStores()
	{
		FakeProvider mail = new("mail", [BackendRole.MailStore, BackendRole.MailSubmit]);
		FakeProvider rest = new("rest", [BackendRole.Calendar, BackendRole.Contacts]);
		CompositeBackendSession session = new(Registry(mail, rest), Gateway, "user@x",
			[
				new ResolvedRole(BackendRole.MailStore, "mail", null, Gateway),
				new ResolvedRole(BackendRole.MailSubmit, "mail", null, Gateway),
				new ResolvedRole(BackendRole.Calendar, "rest", null, Gateway),
				new ResolvedRole(BackendRole.Contacts, "rest", null, Gateway)
			], []);

		// One connection per provider, carrying exactly the roles assigned to it.
		Assert.Equal(1, mail.Connections);
		Assert.Equal(1, rest.Connections);
		Assert.Equal([BackendRole.MailStore, BackendRole.MailSubmit], mail.LastAssignedRoles);
		Assert.Equal([BackendRole.Calendar, BackendRole.Contacts], rest.LastAssignedRoles);

		Assert.Equal(3, session.Stores.Count); // MailSubmit contributes no store
		Assert.NotNull(session.MailStore);
		Assert.NotNull(session.MailSubmit);
		Assert.Equal("Calendar", session.GetStoreForClass("Calendar")!.EasClass);

		// Key dispatch goes through OwnsBackendKey; read-only routes to the owning store.
		Assert.Equal("Email", session.GetStoreForBackendKey("mail-MailStore:INBOX")!.EasClass);
		Assert.Null(session.GetStoreForBackendKey("jmap:INBOX"));
		Assert.True(session.IsReadOnlyFolder("rest-Calendar:shared"));
		Assert.False(session.IsReadOnlyFolder("rest-Calendar:own"));
	}

	[Fact]
	public void Session_RequiresBothMailRoles()
	{
		FakeProvider store = new("store", [BackendRole.MailStore]);
		FakeProvider submit = new("submit", [BackendRole.MailSubmit]);
		Assert.Throws<InvalidOperationException>(() => new CompositeBackendSession(
			Registry(store, submit), Gateway, null,
			[new ResolvedRole(BackendRole.MailStore, "store", null, Gateway)], []));
		Assert.Throws<InvalidOperationException>(() => new CompositeBackendSession(
			Registry(store, submit), Gateway, null,
			[new ResolvedRole(BackendRole.MailSubmit, "submit", null, Gateway)], []));
	}

	[Fact]
	public async Task Session_Dispose_DisposesEveryConnectionResource()
	{
		FakeProvider mail = new("mail", [BackendRole.MailStore, BackendRole.MailSubmit]);
		CompositeBackendSession session = new(Registry(mail), Gateway, null,
			[
				new ResolvedRole(BackendRole.MailStore, "mail", null, Gateway),
				new ResolvedRole(BackendRole.MailSubmit, "mail", null, Gateway)
			], []);
		await session.DisposeAsync();
		Assert.True(mail.LastResource!.Disposed);
	}

	private sealed class FakeProvider(string name, BackendRole[] roles) : IBackendProvider
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

		public IBackendConnection CreateConnection(BackendConnectionContext context)
		{
			Connections++;
			LastAssignedRoles = context.Roles.Select(r => r.Role).ToList();
			LastResource = new FakeResource();
			List<IContentStore> stores = context.Roles
				.Where(r => r.Role is not (BackendRole.MailSubmit or BackendRole.Oof))
				.Select(IContentStore (r) => new FakeStore($"{name}-{r.Role}", r.Role.ToString()))
				.ToList();
			return new BackendConnection(
				stores,
				context.Roles.Any(r => r.Role == BackendRole.MailSubmit) ? new FakeSubmit() : null,
				ownedResources: [LastResource]);
		}
	}

	private sealed class FakeResource : IAsyncDisposable
	{
		public bool Disposed { get; private set; }

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeSubmit : IMailSubmitOperations
	{
		public Task SendAsync(byte[] mime, CancellationToken ct) => Task.CompletedTask;
	}

	/// <summary>Store with a "{prefix}:" key space; the MailStore one also does mail-store ops.</summary>
	private sealed class FakeStore(string prefix, string role) : IContentStore, IMailStoreOperations,
		ICalendarOperations, IReadOnlyCollectionSource
	{
		public string EasClass => role switch
		{
			"MailStore" => "Email",
			"Calendar" => "Calendar",
			"Contacts" => "Contacts",
			_ => role
		};

		public bool OwnsBackendKey(string backendKey) =>
			backendKey.StartsWith(prefix + ":", StringComparison.Ordinal);

		public bool IsReadOnlyCollection(string folderBackendKey) => folderBackendKey.EndsWith(":shared");

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
			string folderBackendKey, ContentFilter filter, CancellationToken ct) => throw new NotSupportedException();

		public Task<BackendItem?> GetItemAsync(
			string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(string ItemKey, string Revision)> CreateItemAsync(
			string folderBackendKey, XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<string> UpdateItemAsync(
			string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteItemAsync(
			string folderBackendKey, string itemKey, CancellationToken ct, bool permanent = false) =>
			throw new NotSupportedException();

		public Task<string> MoveItemAsync(
			string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteFolderAsync(string backendKey, CancellationToken ct) => throw new NotSupportedException();

		public Task<IReadOnlyList<string>> WaitForChangesAsync(
			IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SaveToSentAsync(byte[] mime, CancellationToken ct) => throw new NotSupportedException();

		public Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SetAnsweredAsync(string folderBackendKey, string itemKey, bool forwarded, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
			string? folderBackendKey, string freeText, DateTime? sinceUtc, int maxResults, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct) => throw new NotSupportedException();

		public Task<string?> RespondToMeetingAsync(
			string calendarFolderBackendKey, string eventUid, int userResponse, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<string?> GetRawEventAsync(string folderBackendKey, string itemKey, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct) => throw new NotSupportedException();
	}
}
