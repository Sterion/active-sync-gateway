using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Folder &amp; provision conformance.
///   <list type="bullet">
///     <item>FolderSync must replay the previous generation on a lost response instead of
///       forcing a full hierarchy resync (Status 9 → key 0).</item>
///     <item>A folder-op failure must carry a meaningful status — a malformed request is
///       Status 10 and a backend/transport failure is Status 6, not "system folder" (3) or an
///       uncaught HTTP 500.</item>
///     <item>FolderCreate must honour the requested Type instead of silently creating a mail
///       folder for a calendar/contacts/tasks request.</item>
///     <item>FolderUpdate must not silently ignore a requested parent change (folder move) —
///       renaming in place while reporting success leaves the client believing it moved the folder,
///       and the next FolderSync re-asserts the old parent (churn).</item>
///   </list>
/// </summary>
public sealed class FolderConformanceTests : IDisposable
{
	private static readonly XNamespace FH = EasNamespaces.FolderHierarchy;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	// ---- FolderSync replay on a lost response ----------------------------------

	[Fact]
	public async Task FolderSync_ReplaysPreviousGeneration_InsteadOfForcingFullResync()
	{
		FolderSyncHandler handler = new(_harness.Folders, NullLogger<FolderSyncHandler>.Instance);

		// Generation 1: initial sync (key 0 → key 1) acknowledges the starting hierarchy.
		await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:INBOX", DisplayName = "Inbox", Type = FolderType.Inbox, EasClass = EasClass.Email },
			new BackendFolder { BackendKey = "imap:Sent", DisplayName = "Sent", Type = FolderType.SentItems, EasClass = EasClass.Email });
		XDocument? initial = await _harness.RunAsync(handler, "FolderSync", FolderSyncRequest("0"));
		Assert.Equal("1", initial?.Root?.Element(FH + "SyncKey")?.Value);

		// Generation 2: a new folder appears; the client acks key 1 and the server advances to
		// key 2 — but imagine the response carrying key 2 never reaches the client.
		await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:INBOX", DisplayName = "Inbox", Type = FolderType.Inbox, EasClass = EasClass.Email },
			new BackendFolder { BackendKey = "imap:Sent", DisplayName = "Sent", Type = FolderType.SentItems, EasClass = EasClass.Email },
			new BackendFolder { BackendKey = "imap:Archive", DisplayName = "Archive", Type = FolderType.UserMail, EasClass = EasClass.Email });
		XDocument? gen2 = await _harness.RunAsync(handler, "FolderSync", FolderSyncRequest("1"));
		Assert.Equal("2", gen2?.Root?.Element(FH + "SyncKey")?.Value);

		// The client, never having seen key 2, retries with its last acked key (1). That must be
		// replayed — the full current hierarchy re-emitted as Adds under the current key 2 — not a
		// Status 9 that restarts the whole hierarchy from key 0.
		XDocument? replay = await _harness.RunAsync(handler, "FolderSync", FolderSyncRequest("1"));
		Assert.Equal("1", replay?.Root?.Element(FH + "Status")?.Value);
		Assert.Equal("2", replay?.Root?.Element(FH + "SyncKey")?.Value);
		Assert.Equal(3, replay?.Root?.Element(FH + "Changes")?.Elements(FH + "Add").Count());
	}

	// ---- FolderSync backend-failure mapping --------------------------------

	// Unlike its three sibling folder handlers (FolderModifyHandlerBase's ExecuteAsync/
	// refresh/commit path), FolderSync had no backend-failure mapping — the handler that runs on
	// every device on every reconnect. A transport failure reaching the hierarchy refresh/commit
	// must surface as EAS Status 6, not escape raw and become an HTTP 500 the client cannot
	// interpret.
	[Fact]
	public async Task FolderSync_BackendTransportFailure_YieldsStatus6_NotAnUncaughtError()
	{
		await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:INBOX", DisplayName = "Inbox", Type = FolderType.Inbox, EasClass = EasClass.Email });

		FolderSyncHandler handler = new(_harness.Folders, NullLogger<FolderSyncHandler>.Instance);

		// Build the context the normal way (the device row is resolved while the DB is healthy).
		EasContext context = await _harness.NewContextAsync("FolderSync");

		// Break the connection the request's own DB operations rely on — a genuine backend/
		// transport failure, not the FolderSyncKey race BackendException the handler already
		// handles via its own try/catch around CommitFolderHierarchyAsync.
		await _harness.Db.Database.GetDbConnection().CloseAsync();

		await handler.HandleAsync(context, CancellationToken.None);

		byte[] responseBytes = ((MemoryStream)context.Http.Response.Body).ToArray();
		XDocument? response = responseBytes.Length == 0 ? null : WbxmlDecoder.Decode(responseBytes);
		Assert.Equal("6", response?.Root?.Element(FH + "Status")?.Value);
	}

	// ---- Folder-op failure status mapping -------------------------------------

	[Fact]
	public async Task FolderCreate_BackendTransportFailure_YieldsStatus6_NotAnUncaughtError()
	{
		// A non-BackendException (e.g. an IMAP socket drop) must surface as EAS Status 6, not
		// escape the handler as an HTTP 500.
		_harness.Session.Store.FolderOpFailWith = () => new InvalidOperationException("IMAP connection dropped");

		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserMail.ToString()),
				new XElement(FH + "DisplayName", "NewFolder"))));

		Assert.Equal("6", response?.Root?.Element(FH + "Status")?.Value);
	}

	[Fact]
	public async Task FolderCreate_MissingDisplayName_YieldsMalformedStatus10_NotSystemFolder3()
	{
		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserMail.ToString()))));

		Assert.Equal("10", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Empty(_harness.Session.Store.CreatedFolders);
	}

	// ---- FolderCreate honours the requested Type ------------------------------

	// ---- FolderCreate ServerId on a lagging post-create listing ------------

	// A backend whose post-create listing does not yet reflect the new folder (Axigen's
	// async indexing lag is the real-world trigger) must NOT be reported to the client as a
	// success with no ServerId — the client would cache a folder it can never address again.
	[Fact]
	public async Task FolderCreate_NotVisibleInThePostCreateListing_IsRetryable_NotFalseSuccess()
	{
		// RecordingStore.Listing is left unset, so ListFoldersAsync throws (the harness default) —
		// FolderService.RefreshAsync falls back to the previously-persisted registry, which cannot
		// contain a folder the backend has not reported yet.
		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserMail.ToString()),
				new XElement(FH + "DisplayName", "Projects"))));

		// The backend genuinely created it...
		Assert.Single(_harness.Session.Store.CreatedFolders);
		// ...but the client must not be told this succeeded with no way to address the folder.
		Assert.NotEqual("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Equal("6", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Null(response?.Root?.Element(FH + "ServerId"));
	}

	[Fact]
	public async Task FolderCreate_CalendarType_WithNoCalendarStore_IsRefused_NotCreatedAsMail()
	{
		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserCalendar.ToString()),
				new XElement(FH + "DisplayName", "MyCalendar"))));

		Assert.Equal("3", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Empty(_harness.Session.Store.CreatedFolders); // never silently filed as a mail folder
	}

	[Fact]
	public async Task FolderCreate_CalendarType_RoutesToTheCalendarStore()
	{
		EasHandlerHarness.RecordingStore calendar = new()
		{
			EasClass = EasClass.Calendar,
			KeyPrefix = "caldav:"
		};
		_harness.Session.SecondaryStore = calendar;
		// The post-create hierarchy refresh must find the new folder for the create to be
		// reported as a success — simulate a calendar backend whose listing keeps up.
		calendar.Listing = [new BackendFolder { BackendKey = "caldav:MyCalendar", DisplayName = "MyCalendar", Type = FolderType.UserCalendar, EasClass = EasClass.Calendar }];

		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserCalendar.ToString()),
				new XElement(FH + "DisplayName", "MyCalendar"))));

		Assert.Equal("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.NotNull(response?.Root?.Element(FH + "ServerId")); // a real success carries a ServerId
		Assert.Single(calendar.CreatedFolders);
		Assert.Empty(_harness.Session.Store.CreatedFolders); // mail store left untouched
	}

	[Fact]
	public async Task FolderCreate_MailType_StillCreatesInTheMailStore()
	{
		// Regression guard: the default class (Email / Type 12) still routes to the mail store.
		// The post-create hierarchy refresh must find the new folder for this to be reported
		// as a success — simulate a mail backend whose listing keeps up.
		_harness.Session.Store.Listing = [new BackendFolder { BackendKey = "imap:Projects", DisplayName = "Projects", Type = FolderType.UserMail, EasClass = EasClass.Email }];

		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserMail.ToString()),
				new XElement(FH + "DisplayName", "Projects"))));

		Assert.Equal("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.NotNull(response?.Root?.Element(FH + "ServerId")); // a real success carries a ServerId
		Assert.Single(_harness.Session.Store.CreatedFolders);
	}

	// A FolderCreate enumerates the whole multi-backend hierarchy exactly ONCE, not twice
	// (ExecuteAsync used to refresh to map the new key to a ServerId, then HandleAsync refreshed
	// again to commit).
	[Fact]
	public async Task FolderCreate_EnumeratesTheHierarchyOnlyOnce()
	{
		// Without a listing that reflects the new folder, the create is retryable (Status 6) —
		// give this test the same happy-path listing so it still measures what it is named for.
		_harness.Session.Store.Listing = [new BackendFolder { BackendKey = "imap:Projects", DisplayName = "Projects", Type = FolderType.UserMail, EasClass = EasClass.Email }];

		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserMail.ToString()),
				new XElement(FH + "DisplayName", "Projects"))));

		Assert.Equal("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Equal(1, _harness.Session.Store.ListFoldersCalls);
	}

	// ---- FolderUpdate honours a parent change --------------------------------

	[Fact]
	public async Task FolderUpdate_WithADifferentParentId_IsRejected_NotSilentlyRenamedInPlace()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:Parent", DisplayName = "Parent", Type = FolderType.UserMail, EasClass = EasClass.Email },
			new BackendFolder { BackendKey = "imap:Child", DisplayName = "Child", ParentBackendKey = "imap:Parent", Type = FolderType.UserMail, EasClass = EasClass.Email });
		UserFolder child = registry.Single(f => f.BackendKey == "imap:Child");

		// The client asks to move Child to the root (ParentId "0") while also renaming it — a
		// FolderUpdate request that legitimately carries both a parent change and a display-name
		// change (MS-ASCMD requires both elements on every FolderUpdate).
		XDocument? response = await _harness.RunAsync(UpdateHandler(), "FolderUpdate",
			new XDocument(new XElement(FH + "FolderUpdate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ServerId", child.ServerId),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "DisplayName", "Renamed"))));

		// Not the generic success status — the client must be told the move did not happen rather
		// than believe it did.
		Assert.NotEqual("1", response?.Root?.Element(FH + "Status")?.Value);
		// And the backend must never have been asked to rename it in place either — a partial
		// "renamed but not moved" outcome is exactly the silent half-success this guards against.
		Assert.Empty(_harness.Session.Store.RenamedFolders);
	}

	[Fact]
	public async Task FolderUpdate_WithTheSameParentId_StillRenames()
	{
		// Regression guard: a FolderUpdate that does NOT ask for a parent change (the common case —
		// a plain rename) must keep working exactly as before.
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:Parent", DisplayName = "Parent", Type = FolderType.UserMail, EasClass = EasClass.Email },
			new BackendFolder { BackendKey = "imap:Child", DisplayName = "Child", ParentBackendKey = "imap:Parent", Type = FolderType.UserMail, EasClass = EasClass.Email });
		UserFolder parent = registry.Single(f => f.BackendKey == "imap:Parent");
		UserFolder child = registry.Single(f => f.BackendKey == "imap:Child");

		XDocument? response = await _harness.RunAsync(UpdateHandler(), "FolderUpdate",
			new XDocument(new XElement(FH + "FolderUpdate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ServerId", child.ServerId),
				new XElement(FH + "ParentId", parent.ServerId),
				new XElement(FH + "DisplayName", "Renamed"))));

		Assert.Equal("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Single(_harness.Session.Store.RenamedFolders);
	}

	[Fact]
	public async Task FolderUpdate_OfARootFolder_WithParentId0_StillRenames()
	{
		// Regression guard: the common "ParentId 0" case for a top-level folder must not be
		// mistaken for a move.
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:INBOX", DisplayName = "Inbox", Type = FolderType.Inbox, EasClass = EasClass.Email });
		UserFolder inbox = registry.Single();

		XDocument? response = await _harness.RunAsync(UpdateHandler(), "FolderUpdate",
			new XDocument(new XElement(FH + "FolderUpdate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ServerId", inbox.ServerId),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "DisplayName", "Renamed"))));

		Assert.Equal("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Single(_harness.Session.Store.RenamedFolders);
	}

	private FolderUpdateHandler UpdateHandler()
	{
		return new FolderUpdateHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<FolderUpdateHandler>.Instance);
	}

	private FolderCreateHandler CreateHandler()
	{
		return new FolderCreateHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<FolderCreateHandler>.Instance);
	}

	private static XDocument FolderSyncRequest(string syncKey)
	{
		return new XDocument(new XElement(FH + "FolderSync", new XElement(FH + "SyncKey", syncKey)));
	}
}
