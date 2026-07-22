using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>FolderSync (MS-ASCMD 2.2.1.5): folder hierarchy synchronization.</summary>
public sealed class FolderSyncHandler(FolderService folders) : IEasCommandHandler
{
	private static readonly XNamespace FH = EasNamespaces.FolderHierarchy;
	public string Command => "FolderSync";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		string clientKey = request?.Root?.Element(FH + "SyncKey")?.Value ?? "0";

		Device device = context.Device;
		bool initial = clientKey == "0";
		bool parsed = int.TryParse(clientKey, out int key);

		// The current key is the normal case; the previous generation (key == N-1) is a lost
		// response we replay (F25). Anything else is an unknown key → Status 9 → restart from 0.
		if (!initial && (!parsed || (key != device.FolderSyncKey && key != device.FolderSyncKey - 1)))
		{
			await context.WriteResponseAsync(new XDocument(
				new XElement(FH + "FolderSync",
					new XElement(FH + "Status", "9")))); // invalid sync key → client restarts from 0
			return;
		}

		// F25: one-generation replay. The client resent its previous key — the response carrying
		// FolderSyncKey N was lost, so it still holds N-1 while we hold N. Rather than force a full
		// resync (Status 9 → key 0), re-emit the CURRENT hierarchy as Adds under the current key
		// without advancing it; clients apply re-Adds idempotently. This mirrors the item Sync
		// path's N-1 replay (PreviousSnapshotCompressed).
		if (!initial && parsed && key == device.FolderSyncKey - 1)
		{
			List<UserFolder> replayRegistry = await folders.RefreshAsync(context.Session, device.UserName, ct);
			FolderHierarchyDiff replayDiff =
				await context.State.ComputeFolderDiffAsync(device, replayRegistry, ct, true);
			await context.WriteResponseAsync(new XDocument(
				new XElement(FH + "FolderSync",
					new XElement(FH + "Status", "1"),
					new XElement(FH + "SyncKey", device.FolderSyncKey.ToString()),
					BuildChanges(replayDiff))));
			return;
		}

		List<UserFolder> registry = await folders.RefreshAsync(context.Session, device.UserName, ct);
		FolderHierarchyDiff diff = await context.State.ComputeFolderDiffAsync(device, registry, ct, initial);

		if (initial || diff.Adds.Count > 0 || diff.Updates.Count > 0 || diff.Deletes.Count > 0)
		{
			int newKey;
			try
			{
				newKey = await context.State.CommitFolderHierarchyAsync(device, registry, ct);
			}
			catch (BackendException) // a pipelined FolderSync won the FolderSyncKey race (A6)
			{
				await context.WriteResponseAsync(new XDocument(
					new XElement(FH + "FolderSync",
						new XElement(FH + "Status", "9")))); // client restarts the hierarchy from 0
				return;
			}

			await context.WriteResponseAsync(new XDocument(
				new XElement(FH + "FolderSync",
					new XElement(FH + "Status", "1"),
					new XElement(FH + "SyncKey", newKey.ToString()),
					BuildChanges(diff))));
		}
		else
		{
			await context.WriteResponseAsync(new XDocument(
				new XElement(FH + "FolderSync",
					new XElement(FH + "Status", "1"),
					new XElement(FH + "SyncKey", device.FolderSyncKey.ToString()),
					new XElement(FH + "Changes", new XElement(FH + "Count", "0")))));
		}
	}

	/// <summary>Renders a hierarchy diff into the FolderSync &lt;Changes&gt; element.</summary>
	private static XElement BuildChanges(FolderHierarchyDiff diff)
	{
		XElement changes = new(FH + "Changes",
			new XElement(FH + "Count",
				(diff.Adds.Count + diff.Updates.Count + diff.Deletes.Count).ToString()));
		foreach (FolderChange add in diff.Adds)
			changes.Add(new XElement(FH + "Add",
				new XElement(FH + "ServerId", add.ServerId),
				new XElement(FH + "ParentId", add.ParentServerId ?? "0"),
				new XElement(FH + "DisplayName", add.DisplayName),
				new XElement(FH + "Type", add.Type.ToString())));
		foreach (FolderChange update in diff.Updates)
			changes.Add(new XElement(FH + "Update",
				new XElement(FH + "ServerId", update.ServerId),
				new XElement(FH + "ParentId", update.ParentServerId ?? "0"),
				new XElement(FH + "DisplayName", update.DisplayName),
				new XElement(FH + "Type", update.Type.ToString())));
		foreach (string deleted in diff.Deletes)
			changes.Add(new XElement(FH + "Delete",
				new XElement(FH + "ServerId", deleted)));
		return changes;
	}
}

/// <summary>FolderCreate / FolderDelete / FolderUpdate — mail folders only.</summary>
public abstract class FolderModifyHandlerBase(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger logger) : IEasCommandHandler
{
	protected static readonly XNamespace FH = EasNamespaces.FolderHierarchy;

	protected FolderService Folders => folders;
	public abstract string Command { get; }

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		if (request?.Root is null)
		{
			context.Http.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		if (options.Value.ReadOnly)
		{
			logger.LogInformation("Read-only: rejecting {Command} of \"{Folder}\" for {User}",
				Command, RequestedFolderName(request.Root), context.Device.UserName);
			await WriteStatusAsync(context, "3", null); // folder operations not permitted
			return;
		}

		string clientKey = request.Root.Element(FH + "SyncKey")?.Value ?? "0";
		if (!int.TryParse(clientKey, out int key) || key != context.Device.FolderSyncKey)
		{
			await WriteStatusAsync(context, "9", null);
			return;
		}

		string? newBackendKey;
		try
		{
			newBackendKey = await ExecuteAsync(context, request.Root, ct);
		}
		catch (FolderOperationException ex) // a client error that knows its own EAS status (F26)
		{
			logger.LogInformation("{Command} rejected for {User}: {Reason} (Status {Status})",
				Command, context.Device.UserName, ex.Message, ex.Status);
			await WriteStatusAsync(context, ex.Status, null);
			return;
		}
		catch (BackendException)
		{
			await WriteStatusAsync(context, "3", null); // special/system folder or unsupported store
			return;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A backend/transport failure (e.g. IMAP down) must surface as EAS Status 6, not escape
			// to the endpoint as an HTTP 500 the client cannot interpret (F26).
			logger.LogError(ex, "{Command} failed for {User}", Command, context.Device.UserName);
			await WriteStatusAsync(context, "6", null);
			return;
		}

		// F28: ONE hierarchy enumeration per FolderCreate. ExecuteAsync used to run its own
		// Folders.RefreshAsync just to map the new backend key to a ServerId, and this refresh ran
		// a second — two full multi-backend enumerations. ExecuteAsync now returns the backend key
		// and the new folder's ServerId is resolved from this single refresh.
		List<UserFolder> registry = await Folders.RefreshAsync(context.Session, context.Device.UserName, ct);
		int newKey;
		try
		{
			newKey = await context.State.CommitFolderHierarchyAsync(context.Device, registry, ct);
		}
		catch (BackendException) // a pipelined FolderSync won the FolderSyncKey race (A6)
		{
			await WriteStatusAsync(context, "9", null);
			return;
		}

		string? newServerId = newBackendKey is null
			? null
			: registry.FirstOrDefault(f => f.BackendKey == newBackendKey)?.ServerId;
		logger.LogInformation("{Command} \"{Folder}\" for {User}",
			Command, RequestedFolderName(request.Root), context.Device.UserName);
		await WriteStatusAsync(context, "1", newKey.ToString(), newServerId);
	}

	/// <summary>Folder name (or ServerId for deletes) from the request, for log headlines.</summary>
	private string RequestedFolderName(XElement root)
	{
		return root.Element(FH + "DisplayName")?.Value ?? root.Element(FH + "ServerId")?.Value ?? "?";
	}

	/// <summary>
	///   Performs the backend change. Returns the new folder's BACKEND KEY for FolderCreate (the
	///   base handler maps it to a ServerId from its own single hierarchy refresh — F28); null for
	///   Delete/Update, which create nothing.
	/// </summary>
	protected abstract Task<string?> ExecuteAsync(EasContext context, XElement root, CancellationToken ct);

	/// <summary>
	///   Resolves a folder a modifying operation is about to touch. A folder that does not exist
	///   yields a <see cref="FolderOperationException" /> carrying <paramref name="notFoundStatus" />
	///   (Status 4 "folder does not exist", or 5 "parent does not exist" for a create) — NOT the
	///   generic "system folder" Status 3 (F26). A folder the session reports read-only (a
	///   shared-collection grant) still throws <see cref="BackendException" /> → Status 3, the same
	///   answer an unmodifiable system folder gets: the client must not retry either.
	/// </summary>
	protected async Task<(UserFolder Folder, IContentStore Store)> ResolveWritableAsync(
		EasContext context, string serverId, CancellationToken ct, string notFoundStatus = "4")
	{
		(UserFolder Folder, IContentStore Store) resolved =
			await Folders.ResolveCollectionAsync(context.Session, context.Device.UserName, serverId, ct)
			?? throw new FolderOperationException(notFoundStatus, $"Unknown folder \"{serverId}\"");
		if (!WritePermission.IsBlocked(context, options.Value, resolved.Folder))
			return resolved;
		logger.LogInformation("Read-only folder: rejecting {Command} of \"{Folder}\" for {User}",
			Command, resolved.Folder.DisplayName, context.Device.UserName);
		throw new BackendException($"Folder \"{resolved.Folder.DisplayName}\" is granted read-only.");
	}

	private async Task WriteStatusAsync(EasContext context, string status, string? syncKey, string? serverId = null)
	{
		XElement root = new(FH + Command, new XElement(FH + "Status", status));
		if (syncKey is not null)
			root.Add(new XElement(FH + "SyncKey", syncKey));
		if (serverId is not null)
			root.Add(new XElement(FH + "ServerId", serverId));
		await context.WriteResponseAsync(new XDocument(root));
	}
}

/// <summary>
///   A folder-operation error that already knows the EAS status the client should see
///   (MS-ASCMD FolderCreate/Delete/Update Status: 10 malformed request, 5 parent missing,
///   4 folder missing, …). Distinct from <see cref="BackendException" />, which the base handler
///   maps to the generic "cannot be modified" Status 3 — so a client is told what actually went
///   wrong instead of "system folder" (F26).
/// </summary>
internal sealed class FolderOperationException(string status, string message) : Exception(message)
{
	public string Status { get; } = status;
}

public sealed class FolderCreateHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<FolderCreateHandler> logger)
	: FolderModifyHandlerBase(folders, options, logger)
{
	public override string Command => "FolderCreate";

	protected override async Task<string?> ExecuteAsync(EasContext context, XElement root, CancellationToken ct)
	{
		string parentId = root.Element(FH + "ParentId")?.Value ?? "0";
		string displayName = root.Element(FH + "DisplayName")?.Value
		                     ?? throw new FolderOperationException("10", "Missing DisplayName");

		// F27: honour the requested Type. A client creating a calendar (13), contacts (14) or
		// tasks (15) folder must NOT get a mail folder silently created and reported as success.
		// Route to the store for the requested class; a class with no configured store (or one
		// without folder ops) falls through to Status 3 rather than being misfiled as mail.
		int type = int.TryParse(root.Element(FH + "Type")?.Value, out int t) ? t : EasFolderType.UserMail;
		string easClass = ClassForFolderType(type);
		IContentStore? store = context.Session.GetStoreForClass(easClass);
		// K58: folder mutation is an optional capability. A store without it (or an unconfigured
		// class → null) throws BackendException, which the base handler turns into Status 3 — the
		// same answer as an unmodifiable system folder.
		IFolderOperations folderOps = store as IFolderOperations
			?? throw new BackendException($"The {easClass} store does not support folder creation.");
		string? parentBackendKey = null;
		if (parentId != "0")
			// A read-only grant on the parent covers its subtree: creating a child inside it
			// is a write to the shared collection. A missing parent is Status 5, not "system folder".
			parentBackendKey = (await ResolveWritableAsync(context, parentId, ct, "5")).Folder.BackendKey;

		// Return the new folder's backend key; the base handler's single hierarchy refresh (F28)
		// registers it and resolves the ServerId for the response.
		return await folderOps.CreateFolderAsync(parentBackendKey, displayName, ct);
	}

	/// <summary>
	///   Maps a requested MS-ASCMD folder Type to the EAS content class that owns it. The user-*
	///   types route to their class; Type 12 (UserMail) and anything else fall to mail (the
	///   historical default).
	/// </summary>
	private static string ClassForFolderType(int type)
	{
		return type switch
		{
			EasFolderType.UserCalendar => EasClass.Calendar,
			EasFolderType.UserContacts => EasClass.Contacts,
			EasFolderType.UserTasks => EasClass.Tasks,
			_ => EasClass.Email
		};
	}
}

public sealed class FolderDeleteHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<FolderDeleteHandler> logger)
	: FolderModifyHandlerBase(folders, options, logger)
{
	public override string Command => "FolderDelete";

	protected override async Task<string?> ExecuteAsync(EasContext context, XElement root, CancellationToken ct)
	{
		string serverId = root.Element(FH + "ServerId")?.Value
		                  ?? throw new FolderOperationException("10", "Missing ServerId");
		(UserFolder Folder, IContentStore Store) resolved = await ResolveWritableAsync(context, serverId, ct);
		IFolderOperations folderOps = resolved.Store as IFolderOperations
			?? throw new BackendException("This folder's store does not support folder deletion.");
		await folderOps.DeleteFolderAsync(resolved.Folder.BackendKey, ct);
		return null;
	}
}

public sealed class FolderUpdateHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<FolderUpdateHandler> logger)
	: FolderModifyHandlerBase(folders, options, logger)
{
	public override string Command => "FolderUpdate";

	protected override async Task<string?> ExecuteAsync(EasContext context, XElement root, CancellationToken ct)
	{
		string serverId = root.Element(FH + "ServerId")?.Value
		                  ?? throw new FolderOperationException("10", "Missing ServerId");
		string displayName = root.Element(FH + "DisplayName")?.Value
		                     ?? throw new FolderOperationException("10", "Missing DisplayName");
		(UserFolder Folder, IContentStore Store) resolved = await ResolveWritableAsync(context, serverId, ct);
		IFolderOperations folderOps = resolved.Store as IFolderOperations
			?? throw new BackendException("This folder's store does not support folder rename.");
		await folderOps.RenameFolderAsync(resolved.Folder.BackendKey, displayName, ct);
		return null;
	}
}
