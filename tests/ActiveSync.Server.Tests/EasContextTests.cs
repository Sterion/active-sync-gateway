using System.Xml.Linq;
using ActiveSync.Contracts;
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

public sealed class EasContextTests : IDisposable
{
	private static readonly XNamespace Ping = EasNamespaces.Ping;

	private readonly SqliteConnection _connection;
	private readonly SqliteSyncDbContext _db;
	private readonly SyncStateService _state;

	public EasContextTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection).Options;
		_db = new SqliteSyncDbContext(options);
		_db.Database.EnsureCreated();
		_state = new SyncStateService(_db);
	}

	public void Dispose()
	{
		_db.Dispose();
		_connection.Dispose();
	}

	private async Task<EasContext> ContextForAsync(HttpContext http)
	{
		Device device = await _state.GetOrCreateDeviceAsync(
			"u@example.test", "TESTDEVICE01", "TestClient", CancellationToken.None);
		return new EasContext
		{
			Http = http,
			Parameters = new EasRequestParameters { Command = "Ping", DeviceId = device.DeviceId },
			Credentials = new BackendCredentials("u@example.test", "pw"),
			Session = new EasHandlerHarness.StubSession(),
			Device = device,
			State = _state,
			WireLogger = NullLogger.Instance
		};
	}

	private static byte[] EncodePing()
	{
		XDocument doc = new(new XElement(Ping + "Ping",
			new XElement(Ping + "HeartbeatInterval", "60")));
#pragma warning disable VSTHRD103
		return WbxmlEncoder.Encode(doc);
#pragma warning restore VSTHRD103
	}

	// E1: An HTTP/2 request carries a body with no Content-Length and no Transfer-Encoding
	// header (RFC 9113 §8.2.2 forbids Transfer-Encoding). The old body-less test keyed on
	// HTTP/1.1 framing, so ReadRequestAsync mistook every h2 EAS POST for an empty body and
	// returned null — Sync/Ping then fell into their "missing parameters" branches.
	[Fact]
	public async Task ReadRequest_ReadsHttp2BodyWithoutContentLength()
	{
		byte[] body = EncodePing();
		DefaultHttpContext http = new();
		http.Request.Protocol = "HTTP/2";
		http.Request.Body = new MemoryStream(body);
		// HTTP/2: Kestrel exposes no Content-Length for a streamed body and forbids Transfer-Encoding.
		http.Request.ContentLength = null;
		EasContext context = await ContextForAsync(http);

		XDocument? decoded = await context.ReadRequestAsync();

		Assert.NotNull(decoded);
		Assert.Equal("Ping", decoded!.Root!.Name.LocalName);
	}

	// E17: _requestRead was set true before the read, so a failed read poisoned the cache —
	// a retry got a silent "empty body" (null) instead of the real error. After the fix the
	// flag is only set on the happy path, so the failure re-surfaces on every attempt.
	[Fact]
	public async Task ReadRequest_FailedReadDoesNotPoisonCache()
	{
		DefaultHttpContext http = new();
		http.Request.Protocol = "HTTP/1.1";
		http.Request.Body = new ThrowingStream();
		http.Request.ContentLength = 10;
		EasContext context = await ContextForAsync(http);

		await Assert.ThrowsAsync<IOException>(() => context.ReadRequestAsync());
		// The second attempt must see the same error, not a silently-cached null.
		await Assert.ThrowsAsync<IOException>(() => context.ReadRequestAsync());
	}

	// E17: ReadRawBodyAsync neither checked nor set _requestRead, so a handler that called
	// ReadRequestAsync first got an empty second read from the already-consumed body with no
	// diagnostic. After the fix it fails loudly instead.
	[Fact]
	public async Task ReadRawBody_AfterReadRequest_ThrowsInsteadOfEmpty()
	{
		byte[] body = EncodePing();
		DefaultHttpContext http = new();
		http.Request.Protocol = "HTTP/1.1";
		http.Request.Body = new MemoryStream(body);
		http.Request.ContentLength = body.Length;
		EasContext context = await ContextForAsync(http);

		Assert.NotNull(await context.ReadRequestAsync());
		await Assert.ThrowsAsync<InvalidOperationException>(() => context.ReadRawBodyAsync());
	}

	/// <summary>A request body whose read always fails — stands in for a dropped connection.</summary>
	private sealed class ThrowingStream : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => 0; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failed");

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			throw new IOException("read failed");

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			throw new IOException("read failed");

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
