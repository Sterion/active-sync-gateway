using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Protocol.Http;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Infrastructure;

public sealed record EasFolder(string ServerId, string ParentId, string DisplayName, int Type);

public sealed record SyncItem(string ServerId, XElement ApplicationData);

public sealed record SyncResult(
	bool Empty,
	string? Status,
	string? SyncKey,
	List<SyncItem> Adds,
	List<SyncItem> Changes,
	List<string> Deletes,
	List<XElement> Responses,
	bool MoreAvailable)
{
	public static readonly SyncResult NoChanges = new(true, null, null, [], [], [], [], false);
}

/// <summary>
///   A minimal EAS 14.1 client for integration tests: WBXML via the production codec, the
///   base64 binary query form via <see cref="EasRequestParameters.ToBase64" />, Basic auth,
///   policy-key and sync-key tracking. Each instance is one "device".
/// </summary>
public sealed class EasTestClient(HttpClient http, string user, string password, string deviceId)
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;
	private static readonly XNamespace FH = EasNamespaces.FolderHierarchy;
	private static readonly XNamespace PV = EasNamespaces.Provision;
	private static readonly XNamespace CM = EasNamespaces.ComposeMail;
	private static readonly XNamespace P = EasNamespaces.Ping;
	private static readonly XNamespace M = EasNamespaces.Move;
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;
	private string _folderSyncKey = "0";

	public string User => user;
	public string DeviceId => deviceId;
	/// <summary>Settable so policy tests can present a stale-but-valid key on purpose.</summary>
	public uint PolicyKey { get; set; }

	/// <summary>The protocol version this client speaks; 16.x scenarios set "16.1".</summary>
	public string ProtocolVersion { get; set; } = "14.1";
	public Dictionary<string, string> SyncKeys { get; } = new(StringComparer.Ordinal);
	public List<EasFolder> Folders { get; } = [];

	// ---------- transport ----------

	public async Task<HttpResponseMessage> PostRawAsync(
		string command,
		XDocument? body,
		string? collectionId = null,
		string? itemId = null,
		bool saveInSent = false,
		string? attachmentName = null,
		bool usePlainQuery = false,
		CancellationToken ct = default)
	{
		EasRequestParameters parameters = new()
		{
			Command = command,
			ProtocolVersion = ProtocolVersion,
			DeviceId = deviceId,
			DeviceType = "TestClient",
			PolicyKey = PolicyKey,
			CollectionId = collectionId,
			ItemId = itemId,
			SaveInSent = saveInSent,
			AttachmentName = attachmentName
		};

		string url = usePlainQuery
			? $"/Microsoft-Server-ActiveSync?Cmd={command}&User={Uri.EscapeDataString(user)}" +
			  $"&DeviceId={deviceId}&DeviceType=TestClient" +
			  (collectionId is null ? "" : $"&CollectionId={Uri.EscapeDataString(collectionId)}") +
			  (itemId is null ? "" : $"&ItemId={Uri.EscapeDataString(itemId)}") +
			  (attachmentName is null ? "" : $"&AttachmentName={Uri.EscapeDataString(attachmentName)}") +
			  (saveInSent ? "&SaveInSent=T" : "")
			: $"/Microsoft-Server-ActiveSync?{Uri.EscapeDataString(parameters.ToBase64())}";

		using HttpRequestMessage request = new(HttpMethod.Post, url);
		request.Headers.Authorization = new AuthenticationHeaderValue(
			"Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
		request.Headers.TryAddWithoutValidation("MS-ASProtocolVersion", ProtocolVersion);
		request.Headers.TryAddWithoutValidation("User-Agent", "ActiveSyncGateway-IntegrationTests/1.0");
		if (PolicyKey != 0)
			request.Headers.TryAddWithoutValidation("X-MS-PolicyKey", PolicyKey.ToString());

		// Encode is pure CPU/in-memory work (no I/O) — EncodeAsync just calls it internally
		// before writing to a Stream, which doesn't fit ByteArrayContent's byte[] constructor.
#pragma warning disable VSTHRD103
		ByteArrayContent content = new(body is null ? [] : WbxmlEncoder.Encode(body));
#pragma warning restore VSTHRD103
		content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.ms-sync.wbxml");
		request.Content = content;

		return await http.SendAsync(request, ct);
	}

	/// <summary>POSTs and decodes the WBXML response; null for the empty "no changes" answer.</summary>
	public async Task<XDocument?> PostAsync(
		string command, XDocument? body,
		string? collectionId = null, string? itemId = null, bool saveInSent = false,
		CancellationToken ct = default)
	{
		using HttpResponseMessage response = await PostRawAsync(command, body, collectionId, itemId, saveInSent, ct: ct);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
		return bytes.Length == 0 ? null : WbxmlDecoder.Decode(bytes);
	}

	public async Task<HttpResponseMessage> OptionsAsync()
	{
		using HttpRequestMessage request = new(HttpMethod.Options, "/Microsoft-Server-ActiveSync");
		return await http.SendAsync(request);
	}

	// ---------- handshake ----------

	/// <summary>Two-phase Provision (no policies enforced by the gateway; we still handshake like a real device).</summary>
	public async Task ProvisionAsync()
	{
		XDocument? phase1 = await PostAsync("Provision", new XDocument(
			new XElement(PV + "Provision",
				new XElement(PV + "Policies",
					new XElement(PV + "Policy",
						new XElement(PV + "PolicyType", "MS-EAS-Provisioning-WBXML"))))));
		string? tempKey = phase1?.Root?
			.Element(PV + "Policies")?.Element(PV + "Policy")?.Element(PV + "PolicyKey")?.Value;
		Assert.False(string.IsNullOrEmpty(tempKey), "Provision phase 1 returned no PolicyKey");
		PolicyKey = uint.Parse(tempKey!);

		XDocument? phase2 = await PostAsync("Provision", new XDocument(
			new XElement(PV + "Provision",
				new XElement(PV + "Policies",
					new XElement(PV + "Policy",
						new XElement(PV + "PolicyType", "MS-EAS-Provisioning-WBXML"),
						new XElement(PV + "PolicyKey", tempKey),
						new XElement(PV + "Status", "1"))))));
		string? finalKey = phase2?.Root?
			.Element(PV + "Policies")?.Element(PV + "Policy")?.Element(PV + "PolicyKey")?.Value;
		Assert.False(string.IsNullOrEmpty(finalKey), "Provision phase 2 returned no PolicyKey");
		PolicyKey = uint.Parse(finalKey!);
	}

	public async Task<List<EasFolder>> FolderSyncAsync()
	{
		XDocument? response = await PostAsync("FolderSync", new XDocument(
			new XElement(FH + "FolderSync",
				new XElement(FH + "SyncKey", _folderSyncKey))));
		XElement? root = response?.Root;
		Assert.NotNull(root);
		Assert.Equal("1", root.Element(FH + "Status")?.Value);
		_folderSyncKey = root.Element(FH + "SyncKey")?.Value ?? _folderSyncKey;

		XElement? changes = root.Element(FH + "Changes");
		if (changes is not null)
		{
			foreach (XElement add in changes.Elements(FH + "Add"))
				Folders.Add(new EasFolder(
					add.Element(FH + "ServerId")!.Value,
					add.Element(FH + "ParentId")?.Value ?? "0",
					add.Element(FH + "DisplayName")?.Value ?? "",
					int.Parse(add.Element(FH + "Type")?.Value ?? "1")));
			foreach (XElement deleted in changes.Elements(FH + "Delete"))
				Folders.RemoveAll(f => f.ServerId == deleted.Element(FH + "ServerId")?.Value);
			foreach (XElement update in changes.Elements(FH + "Update"))
			{
				string? serverId = update.Element(FH + "ServerId")?.Value;
				Folders.RemoveAll(f => f.ServerId == serverId);
				Folders.Add(new EasFolder(
					serverId!,
					update.Element(FH + "ParentId")?.Value ?? "0",
					update.Element(FH + "DisplayName")?.Value ?? "",
					int.Parse(update.Element(FH + "Type")?.Value ?? "1")));
			}
		}

		return Folders;
	}

	public EasFolder FolderOfType(int type)
	{
		return Folders.FirstOrDefault(f => f.Type == type)
		       ?? throw new InvalidOperationException(
			       $"No folder of type {type}; run FolderSyncAsync first. Have: " +
			       string.Join(", ", Folders.Select(f => $"{f.DisplayName}({f.Type})")));
	}

	/// <summary>Full device handshake: Provision + FolderSync.</summary>
	public async Task HandshakeAsync()
	{
		await ProvisionAsync();
		await FolderSyncAsync();
	}

	public async Task<(string Status, string? ServerId)> FolderCreateAsync(string displayName, string parentId = "0")
	{
		XDocument? response = await PostAsync("FolderCreate", new XDocument(
			new XElement(FH + "FolderCreate",
				new XElement(FH + "SyncKey", _folderSyncKey),
				new XElement(FH + "ParentId", parentId),
				new XElement(FH + "DisplayName", displayName),
				new XElement(FH + "Type", "12"))));
		XElement? root = response?.Root;
		Assert.NotNull(root);
		_folderSyncKey = root.Element(FH + "SyncKey")?.Value ?? _folderSyncKey;
		return (root.Element(FH + "Status")?.Value ?? "?", root.Element(FH + "ServerId")?.Value);
	}

	// ---------- Sync ----------

	public async Task<string> InitialSyncAsync(string collectionId)
	{
		XDocument? response = await PostAsync("Sync", BuildSyncRequest(collectionId, "0", null, null, null));
		XElement collection = GetCollection(response);
		Assert.Equal("1", collection.Element(AS + "Status")?.Value);
		string key = collection.Element(AS + "SyncKey")!.Value;
		SyncKeys[collectionId] = key;
		return key;
	}

	public async Task<SyncResult> SyncAsync(
		string collectionId, XElement? commands = null, int? heartbeatSeconds = null, int windowSize = 100,
		bool deletesAsMoves = true)
	{
		if (!SyncKeys.ContainsKey(collectionId))
			await InitialSyncAsync(collectionId);

		XDocument request = BuildSyncRequest(
			collectionId, SyncKeys[collectionId], commands, heartbeatSeconds, windowSize, deletesAsMoves);
		XDocument? response = await PostAsync("Sync", request);
		return ParseSyncResponse(collectionId, response);
	}

	/// <summary>Sends a 0-byte Sync request (MS-ASCMD empty-request replay).</summary>
	public async Task<SyncResult> EmptySyncAsync(string collectionId)
	{
		XDocument? response = await PostAsync("Sync", null);
		if (response is null)
			return SyncResult.NoChanges;
		// could be a top-level status (e.g. 13) with no collections
		if (response.Root?.Element(AS + "Collections") is null)
			return new SyncResult(false, response.Root?.Element(AS + "Status")?.Value,
				null, [], [], [], [], false);
		return ParseSyncResponse(collectionId, response);
	}

	/// <summary>Repeated Sync until no MoreAvailable; aggregates adds/changes/deletes.</summary>
	public async Task<SyncResult> PullAllAsync(string collectionId)
	{
		List<SyncItem> adds = new();
		List<SyncItem> changes = new();
		List<string> deletes = new();
		SyncResult last;
		do
		{
			last = await SyncAsync(collectionId);
			adds.AddRange(last.Adds);
			changes.AddRange(last.Changes);
			deletes.AddRange(last.Deletes);
		} while (last.MoreAvailable);

		return new SyncResult(false, last.Status, last.SyncKey, adds, changes, deletes, [], false);
	}

	public Task<SyncResult> DeleteItemAsync(string collectionId, string serverId, bool deletesAsMoves = true)
	{
		return SyncAsync(collectionId, new XElement(AS + "Commands",
			new XElement(AS + "Delete", new XElement(AS + "ServerId", serverId))), deletesAsMoves: deletesAsMoves);
	}

	public Task<SyncResult> ChangeItemAsync(string collectionId, string serverId, params XElement[] applicationData)
	{
		return SyncAsync(collectionId, new XElement(AS + "Commands",
			new XElement(AS + "Change",
				new XElement(AS + "ServerId", serverId),
				new XElement(AS + "ApplicationData", applicationData))));
	}

	public Task<SyncResult> AddItemAsync(string collectionId, string clientId, params XElement[] applicationData)
	{
		return SyncAsync(collectionId, new XElement(AS + "Commands",
			new XElement(AS + "Add",
				new XElement(AS + "ClientId", clientId),
				new XElement(AS + "ApplicationData", applicationData))));
	}

	private static XDocument BuildSyncRequest(
		string collectionId, string syncKey, XElement? commands, int? heartbeatSeconds, int? windowSize,
		bool deletesAsMoves = true)
	{
		XElement collection = new(AS + "Collection",
			new XElement(AS + "SyncKey", syncKey),
			new XElement(AS + "CollectionId", collectionId));
		if (syncKey != "0")
		{
			collection.Add(new XElement(AS + "DeletesAsMoves", deletesAsMoves ? "1" : "0"));
			collection.Add(new XElement(AS + "GetChanges"));
			if (windowSize is { } w)
				collection.Add(new XElement(AS + "WindowSize", w.ToString()));
			collection.Add(new XElement(AS + "Options",
				new XElement(AS + "FilterType", "0"),
				new XElement(ASB + "BodyPreference",
					new XElement(ASB + "Type", "1"),
					new XElement(ASB + "TruncationSize", "20000"))));
			if (commands is not null)
				collection.Add(commands);
		}

		XElement sync = new(AS + "Sync", new XElement(AS + "Collections", collection));
		if (heartbeatSeconds is { } hb)
			sync.Add(new XElement(AS + "HeartbeatInterval", hb.ToString()));
		return new XDocument(sync);
	}

	private SyncResult ParseSyncResponse(string collectionId, XDocument? response)
	{
		if (response is null)
			return SyncResult.NoChanges;
		XElement collection = GetCollection(response);
		string? status = collection.Element(AS + "Status")?.Value;
		string? syncKey = collection.Element(AS + "SyncKey")?.Value;
		if (syncKey is not null)
			SyncKeys[collectionId] = syncKey;

		XElement? commands = collection.Element(AS + "Commands");

		List<SyncItem> Items(string name)
		{
			return commands?.Elements(AS + name)
				.Select(e => new SyncItem(
					e.Element(AS + "ServerId")!.Value,
					e.Element(AS + "ApplicationData") ?? new XElement(AS + "ApplicationData")))
				.ToList() ?? [];
		}

		return new SyncResult(
			false,
			status,
			syncKey,
			Items("Add"),
			Items("Change"),
			commands?.Elements(AS + "Delete")
				.Select(e => e.Element(AS + "ServerId")!.Value).ToList() ?? [],
			collection.Element(AS + "Responses")?.Elements().ToList() ?? [],
			collection.Element(AS + "MoreAvailable") is not null);
	}

	private static XElement GetCollection(XDocument? response)
	{
		XElement? collection = response?.Root?
			.Element(AS + "Collections")?
			.Element(AS + "Collection");
		Assert.NotNull(collection);
		return collection;
	}

	// ---------- mail ----------

	public async Task<XDocument?> SendMailAsync(string mime, bool saveInSent = true)
	{
		XElement mimeElement = new(CM + "Mime", Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)));
		mimeElement.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XElement root = new(CM + "SendMail",
			new XElement(CM + "ClientId", Guid.NewGuid().ToString("N")));
		if (saveInSent)
			root.Add(new XElement(CM + "SaveInSentItems"));
		root.Add(mimeElement);
		return await PostAsync("SendMail", new XDocument(root));
	}

	public async Task<XDocument?> SmartReplyAsync(string mime, string folderId, string itemId)
	{
		XElement mimeElement = new(CM + "Mime", Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)));
		mimeElement.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XElement root = new(CM + "SmartReply",
			new XElement(CM + "ClientId", Guid.NewGuid().ToString("N")),
			new XElement(CM + "Source",
				new XElement(CM + "FolderId", folderId),
				new XElement(CM + "ItemId", itemId)),
			new XElement(CM + "SaveInSentItems"),
			mimeElement);
		return await PostAsync("SmartReply", new XDocument(root));
	}

	public static string BuildMime(string from, string to, string subject, string body)
	{
		return $"From: {from}\r\nTo: {to}\r\nSubject: {subject}\r\n" +
		       $"Date: {DateTime.UtcNow:R}\r\nMessage-ID: <{Guid.NewGuid():N}@test.local>\r\n" +
		       $"MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}\r\n";
	}

	// ---------- ping / move / fetch ----------

	public async Task<(string Status, List<string> ChangedFolders)> PingAsync(
		int heartbeatSeconds, params string[] collectionIds)
	{
		XDocument? response = await PostAsync("Ping", new XDocument(
			new XElement(P + "Ping",
				new XElement(P + "HeartbeatInterval", heartbeatSeconds.ToString()),
				new XElement(P + "Folders",
					collectionIds.Select(id => new XElement(P + "Folder",
						new XElement(P + "Id", id),
						new XElement(P + "Class", "Email")))))));
		XElement? root = response?.Root;
		Assert.NotNull(root);
		return (root.Element(P + "Status")?.Value ?? "?",
			root.Element(P + "Folders")?.Elements(P + "Folder").Select(f => f.Value).ToList() ?? []);
	}

	public async Task<XDocument?> MoveItemsAsync(string srcMsgId, string srcFldId, string dstFldId)
	{
		return await PostAsync("MoveItems", new XDocument(
			new XElement(M + "MoveItems",
				new XElement(M + "Move",
					new XElement(M + "SrcMsgId", srcMsgId),
					new XElement(M + "SrcFldId", srcFldId),
					new XElement(M + "DstFldId", dstFldId)))));
	}

	public async Task<(string Status, string? ContentType, byte[]? Data)> FetchAttachmentAsync(string fileReference)
	{
		XDocument? response = await PostAsync("ItemOperations", new XDocument(
			new XElement(IO + "ItemOperations",
				new XElement(IO + "Fetch",
					new XElement(IO + "Store", "Mailbox"),
					new XElement(ASB + "FileReference", fileReference)))));
		XElement? fetch = response?.Root?.Element(IO + "Response")?.Element(IO + "Fetch");
		Assert.NotNull(fetch);
		string status = fetch.Element(IO + "Status")?.Value ?? "?";
		XElement? properties = fetch.Element(IO + "Properties");
		string? data = properties?.Element(IO + "Data")?.Value;
		return (status,
			properties?.Element(ASB + "ContentType")?.Value,
			data is null ? null : Convert.FromBase64String(data));
	}

	public async Task<string> EmptyFolderContentsAsync(string collectionId)
	{
		XDocument? response = await PostAsync("ItemOperations", new XDocument(
			new XElement(IO + "ItemOperations",
				new XElement(IO + "EmptyFolderContents",
					new XElement(AS + "CollectionId", collectionId)))));
		XElement? empty = response?.Root?.Element(IO + "Response")?.Element(IO + "EmptyFolderContents");
		Assert.NotNull(empty);
		return empty.Element(IO + "Status")?.Value ?? "?";
	}

	public async Task<byte[]> GetAttachmentAsync(string fileReference)
	{
		using HttpResponseMessage response = await PostRawAsync("GetAttachment", null, attachmentName: fileReference);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadAsByteArrayAsync();
	}
}
