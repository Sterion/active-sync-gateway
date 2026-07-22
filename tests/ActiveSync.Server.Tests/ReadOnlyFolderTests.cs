using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   F21 — a per-folder read-only grant (`IBackendSession.IsReadOnlyFolder`, how shared
///   collections are surfaced) must block client writes in EVERY mutating handler, not only
///   in Sync. These drive each handler with a folder the session reports read-only and assert
///   the backend was never asked to perform the mutation.
/// </summary>
public sealed class ReadOnlyFolderTests : IDisposable
{
	private static readonly XNamespace M = EasNamespaces.Move;
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace FH = EasNamespaces.FolderHierarchy;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	[Fact]
	public async Task MoveItems_OutOfAReadOnlyFolder_IsRefused()
	{
		(UserFolder shared, UserFolder inbox) = await TwoFoldersAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(shared.BackendKey);

		XDocument? response = await _harness.RunAsync(
			new MoveItemsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<MoveItemsHandler>.Instance),
			"MoveItems",
			new XDocument(new XElement(M + "MoveItems",
				new XElement(M + "Move",
					new XElement(M + "SrcMsgId", $"{shared.ServerId}:42"),
					new XElement(M + "SrcFldId", shared.ServerId),
					new XElement(M + "DstFldId", inbox.ServerId)))));

		Assert.Equal("5", response?.Root?.Element(M + "Response")?.Element(M + "Status")?.Value);
		Assert.Empty(_harness.Session.Store.Moved);
	}

	[Fact]
	public async Task MoveItems_IntoAReadOnlyFolder_IsRefused()
	{
		(UserFolder shared, UserFolder inbox) = await TwoFoldersAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(shared.BackendKey);

		XDocument? response = await _harness.RunAsync(
			new MoveItemsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<MoveItemsHandler>.Instance),
			"MoveItems",
			new XDocument(new XElement(M + "MoveItems",
				new XElement(M + "Move",
					new XElement(M + "SrcMsgId", $"{inbox.ServerId}:42"),
					new XElement(M + "SrcFldId", inbox.ServerId),
					new XElement(M + "DstFldId", shared.ServerId)))));

		Assert.Equal("5", response?.Root?.Element(M + "Response")?.Element(M + "Status")?.Value);
		Assert.Empty(_harness.Session.Store.Moved);
	}

	[Fact]
	public async Task EmptyFolderContents_OnAReadOnlyFolder_IsRefused()
	{
		(UserFolder shared, _) = await TwoFoldersAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(shared.BackendKey);

		XDocument? response = await _harness.RunAsync(
			new ItemOperationsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<ItemOperationsHandler>.Instance),
			"ItemOperations",
			new XDocument(new XElement(IO + "ItemOperations",
				new XElement(IO + "EmptyFolderContents",
					new XElement(AS + "CollectionId", shared.ServerId)))));

		// F45 split the collapsed status 2 into distinct causes: a read-only grant is now 3
		// (access-denied), leaving 2 to mean "not a mail folder".
		XElement? result = response?.Root?.Element(IO + "Response")?.Element(IO + "EmptyFolderContents");
		Assert.Equal("3", result?.Element(IO + "Status")?.Value);
		Assert.Empty(_harness.Session.Mail.Emptied);
	}

	[Fact]
	public async Task FolderDelete_OfAReadOnlyFolder_IsRefused()
	{
		(UserFolder shared, _) = await TwoFoldersAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(shared.BackendKey);

		XDocument? response = await _harness.RunAsync(
			new FolderDeleteHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<FolderDeleteHandler>.Instance),
			"FolderDelete",
			new XDocument(new XElement(FH + "FolderDelete",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ServerId", shared.ServerId))));

		Assert.Equal("3", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Empty(_harness.Session.Store.DeletedFolders);
	}

	[Fact]
	public async Task FolderUpdate_OfAReadOnlyFolder_IsRefused()
	{
		(UserFolder shared, _) = await TwoFoldersAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(shared.BackendKey);

		XDocument? response = await _harness.RunAsync(
			new FolderUpdateHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<FolderUpdateHandler>.Instance),
			"FolderUpdate",
			new XDocument(new XElement(FH + "FolderUpdate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ServerId", shared.ServerId),
				new XElement(FH + "DisplayName", "Renamed"))));

		Assert.Equal("3", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Empty(_harness.Session.Store.RenamedFolders);
	}

	[Fact]
	public async Task FolderCreate_UnderAReadOnlyParent_IsRefused()
	{
		(UserFolder shared, _) = await TwoFoldersAsync();
		_harness.Session.ReadOnlyBackendKeys.Add(shared.BackendKey);

		XDocument? response = await _harness.RunAsync(
			new FolderCreateHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<FolderCreateHandler>.Instance),
			"FolderCreate",
			new XDocument(new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ParentId", shared.ServerId),
				new XElement(FH + "DisplayName", "Child"))));

		Assert.Equal("3", response?.Root?.Element(FH + "Status")?.Value);
		Assert.Empty(_harness.Session.Store.CreatedFolders);
	}

	[Fact]
	public async Task WritableFolder_IsStillWritable()
	{
		// The guard must not become a blanket refusal: with no grant in play the same
		// requests reach the backend exactly as before.
		(UserFolder a, UserFolder b) = await TwoFoldersAsync();

		XDocument? moved = await _harness.RunAsync(
			new MoveItemsHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<MoveItemsHandler>.Instance),
			"MoveItems",
			new XDocument(new XElement(M + "MoveItems",
				new XElement(M + "Move",
					new XElement(M + "SrcMsgId", $"{a.ServerId}:42"),
					new XElement(M + "SrcFldId", a.ServerId),
					new XElement(M + "DstFldId", b.ServerId)))));
		Assert.Equal("3", moved?.Root?.Element(M + "Response")?.Element(M + "Status")?.Value);
		Assert.Single(_harness.Session.Store.Moved);

		XDocument? deleted = await _harness.RunAsync(
			new FolderDeleteHandler(_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
				NullLogger<FolderDeleteHandler>.Instance),
			"FolderDelete",
			new XDocument(new XElement(FH + "FolderDelete",
				new XElement(FH + "SyncKey", "0"),
				new XElement(FH + "ServerId", a.ServerId))));
		Assert.Equal("1", deleted?.Root?.Element(FH + "Status")?.Value);
		Assert.Single(_harness.Session.Store.DeletedFolders);
	}

	/// <summary>A shared (grantable) folder and an ordinary one, both in the mail store.</summary>
	private async Task<(UserFolder Shared, UserFolder Inbox)> TwoFoldersAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:Shared", "Shared", null, EasFolderType.UserMail, EasClass.Email),
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return (registry.Single(f => f.BackendKey == "imap:Shared"),
			registry.Single(f => f.BackendKey == "imap:INBOX"));
	}
}
