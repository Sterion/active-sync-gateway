using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Item 33 — folder &amp; provision conformance.
///   <list type="bullet">
///     <item>F25: FolderSync must replay the previous generation on a lost response instead of
///       forcing a full hierarchy resync (Status 9 → key 0).</item>
///     <item>F26: a folder-op failure must carry a meaningful status — a malformed request is
///       Status 10 and a backend/transport failure is Status 6, not "system folder" (3) or an
///       uncaught HTTP 500.</item>
///     <item>F27: FolderCreate must honour the requested Type instead of silently creating a mail
///       folder for a calendar/contacts/tasks request.</item>
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

	// ---- F25 -------------------------------------------------------------------------------

	[Fact]
	public async Task FolderSync_ReplaysPreviousGeneration_InsteadOfForcingFullResync()
	{
		FolderSyncHandler handler = new(_harness.Folders);

		// Generation 1: initial sync (key 0 → key 1) acknowledges the starting hierarchy.
		await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email),
			new BackendFolder("imap:Sent", "Sent", null, EasFolderType.SentItems, EasClass.Email));
		XDocument? initial = await _harness.RunAsync(handler, "FolderSync", FolderSyncRequest("0"));
		Assert.Equal("1", initial?.Root?.Element(FH + "SyncKey")?.Value);

		// Generation 2: a new folder appears; the client acks key 1 and the server advances to
		// key 2 — but imagine the response carrying key 2 never reaches the client.
		await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email),
			new BackendFolder("imap:Sent", "Sent", null, EasFolderType.SentItems, EasClass.Email),
			new BackendFolder("imap:Archive", "Archive", null, EasFolderType.UserMail, EasClass.Email));
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

	// ---- F26 -------------------------------------------------------------------------------

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

	// ---- F27 -------------------------------------------------------------------------------

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

		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserCalendar.ToString()),
				new XElement(FH + "DisplayName", "MyCalendar"))));

		Assert.Equal("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Single(calendar.CreatedFolders);
		Assert.Empty(_harness.Session.Store.CreatedFolders); // mail store left untouched
	}

	[Fact]
	public async Task FolderCreate_MailType_StillCreatesInTheMailStore()
	{
		// Regression guard: the default class (Email / Type 12) still routes to the mail store.
		XDocument? response = await _harness.RunAsync(CreateHandler(), "FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", "0"),
				new XElement(FH + "Type", EasFolderType.UserMail.ToString()),
				new XElement(FH + "DisplayName", "Projects"))));

		Assert.Equal("1", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Single(_harness.Session.Store.CreatedFolders);
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
