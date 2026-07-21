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
		if (!initial && (!int.TryParse(clientKey, out int key) || key != device.FolderSyncKey))
		{
			await context.WriteResponseAsync(new XDocument(
				new XElement(FH + "FolderSync",
					new XElement(FH + "Status", "9")))); // invalid sync key → client restarts from 0
			return;
		}

		List<UserFolder> registry = await folders.RefreshAsync(context.Session, device.UserName, ct);
		FolderHierarchyDiff diff = await context.State.ComputeFolderDiffAsync(device, registry, ct, initial);

		if (initial || diff.Adds.Count > 0 || diff.Updates.Count > 0 || diff.Deletes.Count > 0)
		{
			int newKey = await context.State.CommitFolderHierarchyAsync(device, registry, ct);
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

			await context.WriteResponseAsync(new XDocument(
				new XElement(FH + "FolderSync",
					new XElement(FH + "Status", "1"),
					new XElement(FH + "SyncKey", newKey.ToString()),
					changes)));
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

		string? newServerId;
		try
		{
			newServerId = await ExecuteAsync(context, request.Root, ct);
		}
		catch (BackendException)
		{
			await WriteStatusAsync(context, "3", null); // special/system folder or unsupported store
			return;
		}

		List<UserFolder> registry = await Folders.RefreshAsync(context.Session, context.Device.UserName, ct);
		int newKey = await context.State.CommitFolderHierarchyAsync(context.Device, registry, ct);
		logger.LogInformation("{Command} \"{Folder}\" for {User}",
			Command, RequestedFolderName(request.Root), context.Device.UserName);
		await WriteStatusAsync(context, "1", newKey.ToString(), newServerId);
	}

	/// <summary>Folder name (or ServerId for deletes) from the request, for log headlines.</summary>
	private string RequestedFolderName(XElement root)
	{
		return root.Element(FH + "DisplayName")?.Value ?? root.Element(FH + "ServerId")?.Value ?? "?";
	}

	/// <summary>Performs the backend change; returns the new folder's ServerId for FolderCreate.</summary>
	protected abstract Task<string?> ExecuteAsync(EasContext context, XElement root, CancellationToken ct);

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
		                     ?? throw new BackendException("Missing DisplayName");

		IContentStore mailStore = context.Session.GetStoreForClass(EasClass.Email)!;
		string? parentBackendKey = null;
		if (parentId != "0")
		{
			UserFolder parent = await context.State.GetFolderByServerIdAsync(context.Device.UserName, parentId, ct)
			                    ?? throw new BackendException("Unknown parent folder");
			parentBackendKey = parent.BackendKey;
		}

		string backendKey = await mailStore.CreateFolderAsync(parentBackendKey, displayName, ct);
		// Register so the response can carry the assigned ServerId.
		List<UserFolder> registry = await Folders.RefreshAsync(context.Session, context.Device.UserName, ct);
		return registry.FirstOrDefault(f => f.BackendKey == backendKey)?.ServerId;
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
		                  ?? throw new BackendException("Missing ServerId");
		(UserFolder Folder, IContentStore Store) resolved =
			await Folders.ResolveCollectionAsync(context.Session, context.Device.UserName, serverId, ct)
			?? throw new BackendException("Unknown folder");
		await resolved.Store.DeleteFolderAsync(resolved.Folder.BackendKey, ct);
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
		                  ?? throw new BackendException("Missing ServerId");
		string displayName = root.Element(FH + "DisplayName")?.Value
		                     ?? throw new BackendException("Missing DisplayName");
		(UserFolder Folder, IContentStore Store) resolved =
			await Folders.ResolveCollectionAsync(context.Session, context.Device.UserName, serverId, ct)
			?? throw new BackendException("Unknown folder");
		await resolved.Store.RenameFolderAsync(resolved.Folder.BackendKey, displayName, ct);
		return null;
	}
}
