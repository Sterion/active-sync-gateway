using System.Data.Common;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Http;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Drives a single EAS command handler against an in-memory state database and a stub
///   backend session, without an HTTP host. Enough to exercise the handler's own decisions
///   (permission checks, status codes) — the wire format goes through the production WBXML
///   codec so the request the handler reads is the one a device would send.
/// </summary>
public sealed class EasHandlerHarness : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SqliteSyncDbContext _db;
	private readonly TableQueryCounter _folderQueryCounter = new("UserFolders");

	public EasHandlerHarness()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		DbContextOptions<SqliteSyncDbContext> dbOptions = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection)
			.AddInterceptors(_folderQueryCounter)
			.Options;
		_db = new SqliteSyncDbContext(dbOptions);
		_db.Database.EnsureCreated();
		// The harness user's identity row — everything per-user is keyed by UserId now.
		User user = new() { Login = UserName, UpdatedUtc = DateTime.UtcNow };
		_db.Users.Add(user);
		_db.SaveChanges();
		UserId = user.UserId;
		State = new SyncStateService(_db);
		Folders = new FolderService(State, TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLogger<FolderService>.Instance);
	}

	/// <summary>
	///   Number of SELECTs issued against <c>UserFolders</c> since the harness was created — a proxy
	///   for how many times <see cref="FolderService.ResolveCollectionAsync" /> actually hit the DB
	///   (a handler that resolves the same source twice shows up as two queries, not one).
	/// </summary>
	public int FolderResolutionQueries => _folderQueryCounter.Count;

	public const string UserName = "u@example.test";

	/// <summary>The harness user's immutable id (created in the constructor).</summary>
	public int UserId { get; }

	public SyncStateService State { get; }
	public FolderService Folders { get; }

	/// <summary>The tracked state DbContext, so a test can observe SaveChanges (write) count.</summary>
	public SqliteSyncDbContext Db => _db;
	public StubSession Session { get; } = new();
	public ActiveSyncOptions Options { get; } = new();

	public void Dispose()
	{
		_db.Dispose();
		_connection.Dispose();
	}

	/// <summary>
	///   A second context over the same in-memory database, for reading what was actually PERSISTED
	///   (committed to the shared SQLite connection) independently of <see cref="State" />'s tracked,
	///   possibly-unsaved entities.
	/// </summary>
	public SqliteSyncDbContext NewDbContext()
	{
		DbContextOptions<SqliteSyncDbContext> dbOptions = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection).Options;
		return new SqliteSyncDbContext(dbOptions);
	}

	/// <summary>Registers folders and returns the live registry (ServerIds assigned by the store).</summary>
	public Task<List<UserFolder>> RegisterFoldersAsync(params RegistryFolder[] folders)
	{
		return State.RefreshFolderRegistryAsync(UserId, folders, CancellationToken.None);
	}

	/// <summary>
	///   The host-side adapter over the stub mail store — what a handler receives from
	///   <c>FolderService.ResolveCollectionAsync</c> now that the EAS conversion lives host-side.
	/// </summary>
	public Eas.Content.ContentAdapter MailAdapter()
	{
		return Eas.Content.ContentAdapter.For(Session, Session.Store, new EasOptions());
	}

	/// <summary>
	///   One registry folder: the store's typed <see cref="BackendFolder" /> plus the EAS class
	///   the HOST derives from the owning store's alias interface (the contract's folder record
	///   carries no class of its own).
	/// </summary>
	public static RegistryFolder Folder(
		string key, string displayName, FolderType type, string easClass, string? parentKey = null)
	{
		return new RegistryFolder(
			new BackendFolder
			{
				Key = new FolderKey(key),
				DisplayName = displayName,
				ParentKey = parentKey is null ? null : new FolderKey(parentKey),
				Type = type
			}, easClass);
	}

	/// <summary>
	///   Builds a bare <see cref="EasContext" /> (no request body) for tests that drive an internal
	///   handler method directly — e.g. <c>SyncHandler.ApplyClientCommandAsync</c> — rather than a
	///   whole command through <see cref="RunAsync" />.
	/// </summary>
	public async Task<EasContext> NewContextAsync(string command = "Sync")
	{
		DefaultHttpContext http = new();
		http.Response.Body = new MemoryStream();
		Device device = await State.GetOrCreateDeviceAsync(UserId, "TESTDEVICE01", "TestClient", CancellationToken.None);
		return new EasContext
		{
			Http = http,
			Parameters = new EasRequestParameters { Command = command, DeviceId = device.DeviceId },
			Credentials = new BackendCredentials { UserName = UserName, Password = "pw" },
			Session = Session,
			Device = device,
			State = State,
			WireLogger = NullLogger.Instance
		};
	}

	/// <summary>
	///   Runs one command and returns the decoded response document (null for an empty body).
	///   <paramref name="credentialsUserName" /> overrides the "user" identity carried on
	///   <see cref="EasContext.Credentials" /> — purely a logging/metrics label (state resolution
	///   is keyed on <see cref="UserId" />, not this string) — so a test that needs to pick its own
	///   emissions out of the process-global <c>GatewayMetrics</c> meter apart from whatever else
	///   is running concurrently can tag them distinctly. Defaults to the shared <see cref="UserName" />.
	/// </summary>
	public async Task<XDocument?> RunAsync(
		IEasCommandHandler handler, string command, XDocument request, string protocolVersion = "14.1",
		string? credentialsUserName = null)
	{
		// Encode is pure CPU/in-memory work (no I/O) — EncodeAsync just calls it internally
		// before writing to a Stream, and the request body here is a byte[] MemoryStream.
#pragma warning disable VSTHRD103
		byte[] encoded = WbxmlEncoder.Encode(request);
#pragma warning restore VSTHRD103
		DefaultHttpContext http = new();
		http.Request.Body = new MemoryStream(encoded);
		http.Request.ContentLength = encoded.Length;
		MemoryStream responseBody = new();
		http.Response.Body = responseBody;

		Device device = await State.GetOrCreateDeviceAsync(UserId, "TESTDEVICE01", "TestClient", CancellationToken.None);
		EasContext context = new()
		{
			Http = http,
			Parameters = new EasRequestParameters
			{
				Command = command, DeviceId = device.DeviceId, ProtocolVersion = protocolVersion
			},
			Credentials = new BackendCredentials { UserName = credentialsUserName ?? UserName, Password = "pw" },
			Session = Session,
			Device = device,
			State = State,
			WireLogger = NullLogger.Instance
		};

		await handler.HandleAsync(context, CancellationToken.None);
		return responseBody.Length == 0 ? null : WbxmlDecoder.Decode(responseBody.ToArray());
	}

	/// <summary>
	///   A backend session with one recording content store. <see cref="ReadOnlyBackendKeys" />
	///   stands in for a shared-collection grant (`IReadOnlyCollectionSource` in production).
	/// </summary>
	public sealed class StubSession : IBackendSession
	{
		public HashSet<string> ReadOnlyBackendKeys { get; } = new(StringComparer.Ordinal);
		public RecordingStore Store { get; } = new();

		/// <summary>
		///   The mailbox side-operations (`IMailboxOperations`). Kept under the name `Mail` because
		///   that is what every test calls it; the contract's own `Mail` (the mail STORE) is the
		///   explicit interface implementation below.
		/// </summary>
		public RecordingMailOperations Mail { get; } = new();

		public RecordingMailSubmit Submit { get; } = new();

		/// <summary>
		///   An optional second content store for a non-mail class (e.g. Calendar/Contacts/Tasks),
		///   so a test can prove class-aware routing. Null unless a test wires one in.
		/// </summary>
		public RecordingStoreBase? SecondaryStore { get; set; }

		public BackendCredentials Credentials => new() { UserName = UserName, Password = "pw" };
		public int UserId => 1;
		public string? MailAddress => UserName;

		public IReadOnlyList<IContentStore> Stores =>
			SecondaryStore is null ? [Store] : [Store, SecondaryStore];

		// The mail STORE and the mailbox side-operations are two different objects now, so the
		// contract's Mail/Mailbox are implemented explicitly and the class keeps its own names.
		IMailStore IBackendSession.Mail => Store;
		IMailboxOperations IBackendSession.Mailbox => Mail;

		public IMailSubmitOperations MailSubmit => Submit;

		/// <summary>Address-book operations (GAL search). Null unless a test wires one in.</summary>
		public IDirectoryOperations? Contacts { get; set; }

		/// <summary>Calendar/meeting operations (meeting response). Null unless a test wires one in.</summary>
		public IMeetingOperations? Calendar { get; set; }

		/// <summary>Out-of-office backend. Null unless a test wires one in (the accept-and-ignore stub).</summary>
		public IOofBackend? Oof { get; set; }

		/// <summary>The revision-keyed payload cache the host's merge path drives.</summary>
		public SessionPayloadCache PayloadCache { get; } = new();

		public IContentStore? GetStoreForClass(string easClass)
		{
			if (easClass == Store.EasClass)
				return Store;
			if (SecondaryStore is not null && easClass == SecondaryStore.EasClass)
				return SecondaryStore;
			return null;
		}

		public IContentStore? GetStoreForKey(FolderKey key)
		{
			if (Store.OwnsKey(key))
				return Store;
			if (SecondaryStore is not null && SecondaryStore.OwnsKey(key))
				return SecondaryStore;
			return null;
		}

		/// <summary>
		///   The stub declares each store's class rather than deriving it: the recording stores are
		///   deliberately retargetable, and the derivation itself is asserted where it lives
		///   (<c>ContentStoreClasses</c>).
		/// </summary>
		public string EasClassOf(IContentStore store)
		{
			return store is RecordingStoreBase recording ? recording.EasClass : Protocol.EasClass.Email;
		}

		public bool IsReadOnlyFolder(FolderKey folder)
		{
			return ReadOnlyBackendKeys.Contains(folder.Value);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>
	///   The class-agnostic half of a recording store: folder listing, the revision map, deletes
	///   and the long-poll wait. A store's content class is derived from WHICH class alias it
	///   implements, so the per-class halves are separate subclasses below.
	/// </summary>
	public abstract class RecordingStoreBase : IContentStore
	{
		/// <summary>Folder/item keys a handler fetched, as "{folder}/{item}".</summary>
		public List<string> Fetched { get; } = [];

		/// <summary>Item keys a handler asked to update, so a conflict test can assert none happened.</summary>
		public List<string> Updated { get; } = [];

		/// <summary>
		///   The backend revision map returned by <see cref="GetItemRevisionsAsync" /> — the
		///   server→client diff source. Left empty (the default), a getChanges round reports no
		///   changes; populate it to drive Add/Change/Delete emission through the Sync collection loop.
		/// </summary>
		public Dictionary<string, string> Revisions { get; } = new(StringComparer.Ordinal);

		/// <summary>
		///   Item keys that a fetch reports as gone (returns null), standing in for an item that
		///   vanished between the revision listing and the fetch.
		/// </summary>
		public HashSet<string> VanishedKeys { get; } = new(StringComparer.Ordinal);

		/// <summary>
		///   One entry per batched <c>GetItemsAsync</c> the Sync engine issued, each the ordered key
		///   list of that call — lets a test assert the window is fetched in ONE batch rather than a
		///   fetch per item.
		/// </summary>
		public List<IReadOnlyList<string>> BatchFetched { get; } = [];

		/// <summary>Items a handler asked to delete, so a test can assert removal happened.</summary>
		public List<string> Deleted { get; } = [];

		/// <summary>
		///   When set, <see cref="DeleteItemAsync" /> throws this instead of recording — a backend
		///   hiccup on the post-send invite-mail cleanup that must NOT fail the command.
		/// </summary>
		public Func<Exception>? DeleteFailWith { get; set; }

		/// <summary>
		///   Long-poll wait behaviour for Ping/Sync tests: given the requested backend keys, return
		///   the ones that changed. Left null, <see cref="WaitForChangesAsync" /> throws (the default
		///   for handlers that never wait).
		/// </summary>
		public Func<IReadOnlyList<string>, IReadOnlyList<string>>? WaitForChanges { get; set; }

		/// <summary>
		///   A genuinely async alternative to <see cref="WaitForChanges" />, for tests that need a
		///   controllable delay (e.g. proving a losing per-store wait is drained rather than
		///   abandoned). Checked before <see cref="WaitForChanges" />.
		/// </summary>
		public Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>>? WaitForChangesAsyncOverride
		{
			get;
			set;
		}

		/// <summary>
		///   The EAS class this store claims — the STUB SESSION's own answer for it (production
		///   derives the class from the alias interface). Defaults per subclass.
		/// </summary>
		public string EasClass { get; set; } = Protocol.EasClass.Email;

		/// <summary>The backend-key prefix this store owns.</summary>
		public string KeyPrefix { get; set; } = "imap:";

		/// <summary>Number of hierarchy enumerations the handler drove (asserted to be exactly one).</summary>
		public int ListFoldersCalls { get; private set; }

		/// <summary>
		///   When set, <see cref="ListFoldersAsync" /> returns this instead of throwing
		///   <see cref="NotSupportedException" /> — lets a test simulate a backend whose listing
		///   genuinely reflects the current folder set (e.g. right after a create). Left null (the
		///   default), the listing always fails, which is how a real backend's post-create
		///   enumeration missing the just-created folder (Axigen's async indexing lag) is
		///   reproduced.
		/// </summary>
		public IReadOnlyList<BackendFolder>? Listing { get; set; }

		/// <summary>
		///   When set, <see cref="GetItemRevisionsAsync" /> throws this instead of returning the
		///   revision map — a flaky backend during a listing.
		/// </summary>
		public Func<Exception>? GetRevisionsFailWith { get; set; }

		public bool OwnsKey(FolderKey key)
		{
			return key.Value.StartsWith(KeyPrefix, StringComparison.Ordinal);
		}

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
		{
			ListFoldersCalls++;
			if (Listing is { } listing)
				return Task.FromResult(listing);
			throw new NotSupportedException();
		}

		public Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
			FolderKey folder, ContentFilter filter, CancellationToken ct)
		{
			if (GetRevisionsFailWith is { } fail)
				throw fail();
			return Task.FromResult<IReadOnlyDictionary<ItemKey, ItemRevision>>(
				Revisions.ToDictionary(r => new ItemKey(r.Key), r => new ItemRevision(r.Value)));
		}

		public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
		{
			if (DeleteFailWith is { } fail)
				throw fail();
			Deleted.Add($"{folder.Value}/{item.Value}");
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
			IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
		{
			List<string> keys = folders.Select(f => f.Value).ToList();
			if (WaitForChangesAsyncOverride is { } asyncWait)
				return Wrap(asyncWait(keys, ct));
			if (WaitForChanges is { } wait)
				return Task.FromResult<IReadOnlyList<FolderKey>>(
					wait(keys).Select(k => new FolderKey(k)).ToList());
			throw new NotSupportedException();

			// The override's task is the TEST's own (it models a controllable backend wait); the
			// wrapper only retypes its result at the contract boundary, exactly as the pre-typed
			// harness returned it untouched.
#pragma warning disable VSTHRD003
			static async Task<IReadOnlyList<FolderKey>> Wrap(Task<IReadOnlyList<string>> changed)
			{
				return (await changed).Select(k => new FolderKey(k)).ToList();
			}
#pragma warning restore VSTHRD003
		}
	}

	/// <summary>
	///   The mail store: raw RFC822 items plus the move/folder capabilities. Records the mutations
	///   a handler asked for, so a test can assert none happened.
	/// </summary>
	public sealed class RecordingStore : RecordingStoreBase, IMailStore, IItemMoveOperations, IFolderOperations
	{
		public List<string> Moved { get; } = [];
		public List<string> DeletedFolders { get; } = [];
		public List<string> RenamedFolders { get; } = [];
		public List<string> CreatedFolders { get; } = [];

		/// <summary>
		///   When set, the folder mutations (create/rename/delete) throw this instead of recording —
		///   a backend/transport failure that is NOT a <see cref="BackendException" />.
		/// </summary>
		public Func<Exception>? FolderOpFailWith { get; set; }

		/// <summary>
		///   Overrides the raw RFC822 a fetched message carries. The default is a minimal, valid
		///   message whose host-side conversion round-trips through the WBXML codec — the store no
		///   longer produces EAS XML at all, so a test that needs a specific rendered shape supplies
		///   the MIME that produces it.
		/// </summary>
		public Func<string, byte[]>? ItemRfc822 { get; set; }

		/// <summary>Drafts created via <c>CreateDraftAsync</c>, as their raw bytes.</summary>
		public List<byte[]> CreatedDrafts { get; } = [];

		/// <summary>Drafts rewritten via <c>ReplaceDraftAsync</c>, as "{item}" keys.</summary>
		public List<string> ReplacedDrafts { get; } = [];

		public Task<MailItem?> GetItemAsync(
			FolderKey folder, ItemKey item, MailFetchOptions options, CancellationToken ct)
		{
			Fetched.Add($"{folder.Value}/{item.Value}");
			if (VanishedKeys.Contains(item.Value))
				return Task.FromResult<MailItem?>(null);
			return Task.FromResult<MailItem?>(new MailItem
			{
				Rfc822 = ItemRfc822?.Invoke(item.Value) ?? DefaultMessage(item.Value),
				Flags = new MailFlags()
			});
		}

		/// <summary>
		///   Records the batch and mirrors the interface default (loop + per-item null on failure),
		///   so existing Sync tests are behaviourally unchanged while a test can assert the routing.
		/// </summary>
		public async Task<IReadOnlyDictionary<ItemKey, MailItem?>> GetItemsAsync(
			FolderKey folder, IReadOnlyList<ItemKey> items, MailFetchOptions options, CancellationToken ct)
		{
			BatchFetched.Add(items.Select(i => i.Value).ToList());
			Dictionary<ItemKey, MailItem?> fetched = new(items.Count);
			foreach (ItemKey item in items)
			{
				try
				{
					fetched[item] = await GetItemAsync(folder, item, options, ct);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					fetched[item] = null;
				}
			}

			return fetched;
		}

		/// <summary>A minimal, valid RFC822 message — enough for the host to render a real Email item.</summary>
		internal static byte[] DefaultMessage(string itemKey)
		{
			return System.Text.Encoding.ASCII.GetBytes(
				"From: sender@example.test\r\nTo: u@example.test\r\n" +
				$"Subject: {itemKey}\r\nDate: Mon, 1 Jul 2024 10:00:00 +0000\r\n" +
				"Content-Type: text/plain; charset=utf-8\r\n\r\npreview\r\n");
		}

		public Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
			FolderKey folder, MailItem item, CancellationToken ct)
		{
			CreatedDrafts.Add(item.Rfc822.ToArray());
			return Task.FromResult((new ItemKey($"draft-{CreatedDrafts.Count}"), new ItemRevision("000")));
		}

		public Task<ItemRevision> UpdateFlagsAsync(
			FolderKey folder, ItemKey item, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct)
		{
			Updated.Add(item.Value);
			return Task.FromResult(new ItemRevision("updated-rev"));
		}

		public Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
			FolderKey folder, ItemKey item, MailItem value, CancellationToken ct)
		{
			Updated.Add(item.Value);
			ReplacedDrafts.Add(item.Value);
			// A real store's rewrite MOVES the key (IMAP delete+append); the host must not
			// echo-suppress under the new one.
			return Task.FromResult((new ItemKey($"{item.Value}-rewritten"), new ItemRevision("updated-rev")));
		}

		/// <summary>
		///   Mirrors a real backend's MoveItemAsync — the moved item keeps its key and reports
		///   whatever <see cref="RecordingStoreBase.Revisions" /> holds for it (defaulting to "" when
		///   a test hasn't set one), never a manufactured placeholder.
		/// </summary>
		public Task<(ItemKey Key, ItemRevision Revision)> MoveItemAsync(
			FolderKey source, ItemKey item, FolderKey destination, CancellationToken ct)
		{
			Moved.Add($"{source.Value}/{item.Value}->{destination.Value}");
			return Task.FromResult(
				(item, new ItemRevision(Revisions.GetValueOrDefault(item.Value, ""))));
		}

		public Task<FolderKey> CreateFolderAsync(FolderKey? parent, string displayName, CancellationToken ct)
		{
			if (FolderOpFailWith is { } fail)
				throw fail();
			CreatedFolders.Add($"{parent?.Value}/{displayName}");
			return Task.FromResult(new FolderKey($"{KeyPrefix}{displayName}"));
		}

		public Task RenameFolderAsync(FolderKey folder, string newDisplayName, CancellationToken ct)
		{
			if (FolderOpFailWith is { } fail)
				throw fail();
			RenamedFolders.Add(folder.Value);
			return Task.CompletedTask;
		}

		public Task DeleteFolderAsync(FolderKey folder, CancellationToken ct)
		{
			if (FolderOpFailWith is { } fail)
				throw fail();
			DeletedFolders.Add(folder.Value);
			return Task.CompletedTask;
		}
	}

	/// <summary>
	///   A calendar store standing in for a CalDAV/JMAP calendar backend: iCalendar payloads plus
	///   the meeting operations MeetingResponse drives.
	/// </summary>
	public sealed class RecordingCalendarStore : RecordingStoreBase, ICalendarStore, IMeetingOperations
	{
		public RecordingCalendarStore()
		{
			EasClass = Protocol.EasClass.Calendar;
			KeyPrefix = "caldav:";
		}

		/// <summary>The stored event iCalendar a fetch returns (null = the item is gone).</summary>
		public string? RawEvent { get; set; }

		/// <summary>The calendar item key <see cref="RespondToMeetingAsync" /> returns (null = not found).</summary>
		public string? RespondHref { get; set; }

		/// <summary>Meeting responses applied: "{folderKey}/{uid}/{userResponse}".</summary>
		public List<string> Responded { get; } = [];

		/// <summary>The complete payloads a handler wrote, in call order.</summary>
		public List<string> Written { get; } = [];

		public Task<CalendarItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct)
		{
			Fetched.Add($"{folder.Value}/{item.Value}");
			if (VanishedKeys.Contains(item.Value) || RawEvent is null)
				return Task.FromResult<CalendarItem?>(null);
			return Task.FromResult<CalendarItem?>(new CalendarItem { ICalendar = RawEvent });
		}

		public Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
			FolderKey folder, CalendarItem item, CancellationToken ct)
		{
			Written.Add(item.ICalendar);
			return Task.FromResult((new ItemKey($"event-{Written.Count}"), new ItemRevision("created-rev")));
		}

		public Task<ItemRevision> UpdateItemAsync(
			FolderKey folder, ItemKey item, CalendarItem value, ItemRevision? expected, CancellationToken ct)
		{
			Updated.Add(item.Value);
			Written.Add(value.ICalendar);
			return Task.FromResult(new ItemRevision("updated-rev"));
		}

		public Task<ItemKey?> RespondToMeetingAsync(
			FolderKey calendar, string eventUid, MeetingResponseKind response, CancellationToken ct)
		{
			Responded.Add($"{calendar.Value}/{eventUid}/{(int)response}");
			return Task.FromResult(RespondHref is null ? null : (ItemKey?)new ItemKey(RespondHref));
		}

		public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct)
		{
			return Task.FromResult(false);
		}
	}

	/// <summary>Outbound submission; records each SendAsync and can be told to fail.</summary>
	public sealed class RecordingMailSubmit : IMailSubmitOperations
	{
		/// <summary>The MIME blobs actually submitted — a duplicate submit shows up as two entries.</summary>
		public List<byte[]> Sent { get; } = [];

		/// <summary>When set, SendAsync throws this instead of recording (a genuine send failure).</summary>
		public Func<Exception>? FailWith { get; set; }

		public Task SendAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct)
		{
			if (FailWith is { } fail)
				throw fail();
			Sent.Add(rfc822.ToArray());
			return Task.CompletedTask;
		}
	}

	/// <summary>Mailbox side operations; records the folders a handler asked to empty.</summary>
	public sealed class RecordingMailOperations : IMailboxOperations
	{
		public List<string> Emptied { get; } = [];

		/// <summary>MIME blobs successfully filed to Sent.</summary>
		public List<byte[]> Saved { get; } = [];

		/// <summary>When true, SaveToSentAsync throws — the post-submit "best-effort" failure.</summary>
		public bool SaveToSentShouldThrow { get; set; }

		/// <summary>True once SaveToSentAsync was reached (whether or not it was told to throw).</summary>
		public bool SaveToSentAttempted { get; private set; }

		public Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct)
		{
			SaveToSentAttempted = true;
			if (SaveToSentShouldThrow)
				throw new BackendException("save to Sent failed");
			Saved.Add(rfc822.ToArray());
			return Task.CompletedTask;
		}

		/// <summary>The raw MIME a MeetingResponse invite fetch returns; null throws NotSupported.</summary>
		public byte[]? RawMessage { get; set; }

		/// <summary>When set, GetRawMessageAsync throws this — a backend failure mid-request.</summary>
		public Func<Exception>? GetRawFailWith { get; set; }

		/// <summary>Number of times the raw-message fetch was actually invoked.</summary>
		public int GetRawMessageCalls { get; private set; }

		public Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(FolderKey folder, ItemKey item, CancellationToken ct)
		{
			GetRawMessageCalls++;
			if (GetRawFailWith is { } fail)
				throw fail();
			if (RawMessage is null)
				throw new NotSupportedException();
			return Task.FromResult<ReadOnlyMemory<byte>?>(RawMessage);
		}

		/// <summary>
		///   Folder/item keys a handler flagged answered/forwarded, so a test can assert none happened when
		///   the source folder is read-only-blocked.
		/// </summary>
		public List<string> Answered { get; } = [];

		public Task SetAnsweredAsync(FolderKey folder, ItemKey item, bool forwarded, CancellationToken ct)
		{
			Answered.Add($"{folder.Value}/{item.Value}:{forwarded}");
			return Task.CompletedTask;
		}

		/// <summary>
		///   Backend hits a Search/Find will page over. <see cref="SearchAsync" /> returns the first
		///   <c>maxResults</c> of these (the fetch cap the handler passes), so a test can make the
		///   pre-paging hit count differ from the served page size and exercise paging.
		/// </summary>
		public List<(string FolderBackendKey, string ItemKey)> SearchHits { get; } = [];

		/// <summary>Number of times the backend search was actually invoked (zero when paging skips the backend call).</summary>
		public int SearchCalls { get; private set; }

		/// <summary>
		///   When set, <see cref="SearchAsync" /> throws this instead of returning hits — a transient
		///   backend failure mid-search, distinct from a malformed request.
		/// </summary>
		public Func<Exception>? SearchFailWith { get; set; }

		public Task<IReadOnlyList<SearchHit>> SearchAsync(
			FolderKey? folder, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct)
		{
			SearchCalls++;
			if (SearchFailWith is { } fail)
				throw fail();
			IReadOnlyList<SearchHit> page = SearchHits.Take(maxResults)
				.Select(h => new SearchHit { Folder = new FolderKey(h.FolderBackendKey), Item = new ItemKey(h.ItemKey) })
				.ToList();
			return Task.FromResult(page);
		}

		/// <summary>
		///   When set, <see cref="EmptyFolderAsync" /> throws this instead of recording — a backend
		///   hiccup during EmptyFolderContents that must fail just that operation (a Status), not the
		///   whole ItemOperations request.
		/// </summary>
		public Func<Exception>? EmptyFolderFailWith { get; set; }

		public Task EmptyFolderAsync(FolderKey folder, CancellationToken ct)
		{
			if (EmptyFolderFailWith is { } fail)
				throw fail();
			Emptied.Add(folder.Value);
			return Task.CompletedTask;
		}
	}

	/// <summary>Counts SELECTs issued against one table, so a test can assert a DB lookup ran exactly once.</summary>
	private sealed class TableQueryCounter(string tableName) : DbCommandInterceptor
	{
		private int _count;

		public int Count => _count;

		public override InterceptionResult<DbDataReader> ReaderExecuting(
			DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
		{
			if (command.CommandText.Contains(tableName, StringComparison.Ordinal))
				Interlocked.Increment(ref _count);
			return base.ReaderExecuting(command, eventData, result);
		}

		public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
			DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
			CancellationToken cancellationToken = default)
		{
			if (command.CommandText.Contains(tableName, StringComparison.Ordinal))
				Interlocked.Increment(ref _count);
			return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
		}
	}
}
