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

	public EasHandlerHarness()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		DbContextOptions<SqliteSyncDbContext> dbOptions = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection).Options;
		_db = new SqliteSyncDbContext(dbOptions);
		_db.Database.EnsureCreated();
		State = new SyncStateService(_db);
		Folders = new FolderService(State, NullLogger<FolderService>.Instance);
	}

	public const string UserName = "u@example.test";

	public SyncStateService State { get; }
	public FolderService Folders { get; }
	public StubSession Session { get; } = new();
	public ActiveSyncOptions Options { get; } = new();

	public void Dispose()
	{
		_db.Dispose();
		_connection.Dispose();
	}

	/// <summary>Registers folders and returns the live registry (ServerIds assigned by the store).</summary>
	public Task<List<UserFolder>> RegisterFoldersAsync(params BackendFolder[] folders)
	{
		return State.RefreshFolderRegistryAsync(UserName, folders, CancellationToken.None);
	}

	/// <summary>Runs one command and returns the decoded response document (null for an empty body).</summary>
	public async Task<XDocument?> RunAsync(IEasCommandHandler handler, string command, XDocument request)
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

		Device device = await State.GetOrCreateDeviceAsync(UserName, "TESTDEVICE01", "TestClient", CancellationToken.None);
		EasContext context = new()
		{
			Http = http,
			Parameters = new EasRequestParameters { Command = command, DeviceId = device.DeviceId },
			Credentials = new BackendCredentials(UserName, "pw"),
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
		public RecordingMailOperations Mail { get; } = new();

		public BackendCredentials Credentials => new(UserName, "pw");
		public string? MailAddress => UserName;
		public IReadOnlyList<IContentStore> Stores => [Store];
		public IMailStoreOperations MailStore => Mail;
		public IMailSubmitOperations MailSubmit => throw new NotSupportedException();
		public IContactOperations? Contacts => null;
		public ICalendarOperations? Calendar => null;
		public IOofBackend? Oof => null;

		public IContentStore? GetStoreForClass(string easClass)
		{
			return easClass == Store.EasClass ? Store : null;
		}

		public IContentStore? GetStoreForBackendKey(string backendKey)
		{
			return Store.OwnsBackendKey(backendKey) ? Store : null;
		}

		public bool IsReadOnlyFolder(string folderBackendKey)
		{
			return ReadOnlyBackendKeys.Contains(folderBackendKey);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Records the mutations a handler asked for, so a test can assert none happened.</summary>
	public sealed class RecordingStore : IContentStore
	{
		public List<string> Moved { get; } = [];
		public List<string> DeletedFolders { get; } = [];
		public List<string> RenamedFolders { get; } = [];
		public List<string> CreatedFolders { get; } = [];

		public string EasClass => Protocol.EasClass.Email;

		public bool OwnsBackendKey(string backendKey)
		{
			return backendKey.StartsWith("imap:", StringComparison.Ordinal);
		}

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
			string folderBackendKey, ContentFilter filter, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task<BackendItem?> GetItemAsync(
			string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
		{
			return Task.FromResult<BackendItem?>(
				new BackendItem([new XElement(EasNamespaces.AirSync + "Subject", itemKey)]));
		}

		public Task<(string ItemKey, string Revision)> CreateItemAsync(
			string folderBackendKey, XElement applicationData, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task<string> UpdateItemAsync(
			string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task DeleteItemAsync(string folderBackendKey, string itemKey, CancellationToken ct, bool permanent = false)
		{
			throw new NotSupportedException();
		}

		public Task<string> MoveItemAsync(
			string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
		{
			Moved.Add($"{sourceFolderBackendKey}/{itemKey}->{destinationFolderBackendKey}");
			return Task.FromResult(itemKey);
		}

		public Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct)
		{
			CreatedFolders.Add($"{parentBackendKey}/{displayName}");
			return Task.FromResult($"imap:{displayName}");
		}

		public Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct)
		{
			RenamedFolders.Add(backendKey);
			return Task.CompletedTask;
		}

		public Task DeleteFolderAsync(string backendKey, CancellationToken ct)
		{
			DeletedFolders.Add(backendKey);
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<string>> WaitForChangesAsync(
			IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct)
		{
			throw new NotSupportedException();
		}
	}

	/// <summary>Mail-store side operations; records the folders a handler asked to empty.</summary>
	public sealed class RecordingMailOperations : IMailStoreOperations
	{
		public List<string> Emptied { get; } = [];

		public Task SaveToSentAsync(byte[] mime, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task SetAnsweredAsync(string folderBackendKey, string itemKey, bool forwarded, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
			string? folderBackendKey, string freeText, DateTime? sinceUtc, int maxResults, CancellationToken ct)
		{
			throw new NotSupportedException();
		}

		public Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct)
		{
			Emptied.Add(folderBackendKey);
			return Task.CompletedTask;
		}
	}
}
