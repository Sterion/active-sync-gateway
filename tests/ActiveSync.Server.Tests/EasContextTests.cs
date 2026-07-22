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
}
