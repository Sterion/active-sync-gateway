using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C9 — the admin backend probe's failure detail. The SSRF is acceptable (an admin who sets
///   the backend URL permanently can already make the gateway connect anywhere, and the probe
///   is capped at 5 s); returning <c>GetBaseException().Message</c> is not. It turns the probe
///   into a precise internal-network scanner — refused, timed out, DNS failure and TLS mismatch
///   are four distinguishable answers — and can surface file paths out of the exception text.
///
///   C2 — the boolean <c>reachable</c> answer itself is the oracle. The role's STORED BaseUrl
///   ("http://127.0.0.1:1/", nothing listens there) is deliberately unreachable so a test that
///   still observes <c>reachable:true</c> proves the request body's Settings — not the stored
///   configuration — chose the probed host.
/// </summary>
public sealed class BackendProbeTests
{
	private static readonly Dictionary<string, string?> CalDavRole = new()
	{
		["ActiveSync:Backends:Calendar:Provider"] = "caldav",
		["ActiveSync:Backends:Calendar:BaseUrl"] = "http://127.0.0.1:1/"
	};

	private static async Task<WebUiHost> AdminHostAsync()
	{
		return await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new UserOptions { Admin = true })), CalDavRole);
	}

	[Fact]
	public async Task RefusedConnection_ReportsAClosedOutcome_NotTheRawExceptionText()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		// Nothing listens on port 1 of the loopback interface: an immediate, deterministic
		// connection failure, which is exactly the signal a network scan reads. The role's
		// stored BaseUrl already points there (see CalDavRole) — no request-body override needed.
		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/backends/Calendar/test", new { });

		JsonElement body = await host.ReadJsonAsync(response);
		Assert.False(body.GetProperty("reachable").GetBoolean());
		string detail = body.GetProperty("detail").GetString()!;
		Assert.Equal("The server could not be reached.", detail);
	}

	[Fact]
	public async Task UnknownProvider_ReportsAFixedMessage()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync("/admin/api/backends/Calendar/test", new
		{
			provider = "nosuchprovider"
		});

		JsonElement body = await host.ReadJsonAsync(response);
		Assert.False(body.GetProperty("supported").GetBoolean());
		Assert.False(body.GetProperty("reachable").GetBoolean());
	}

	/// <summary>
	///   C2: a request-body Settings override must never choose the probed host. The role is
	///   stored pointing at a closed port (unreachable); the request asks the probe to hit a real,
	///   answering local listener instead. If the override were honored, <c>reachable</c> would
	///   come back true for a host the role was never actually configured to reach — an SSRF-style
	///   oracle any admin caller could aim at an arbitrary internal address.
	/// </summary>
	[Fact]
	public async Task SettingsOverrideInTheRequestBody_IsIgnored_OnlyTheStoredHostIsProbed()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);
		using PlainHttpServer answering = PlainHttpServer.Start();

		HttpResponseMessage response = await client.PostAsJsonAsync("/admin/api/backends/Calendar/test", new
		{
			settings = new Dictionary<string, string?> { ["BaseUrl"] = answering.Url }
		});

		JsonElement body = await host.ReadJsonAsync(response);
		Assert.False(body.GetProperty("reachable").GetBoolean());
	}

	/// <summary>A minimal plain-HTTP server on 127.0.0.1 that answers one request with HTTP 200.</summary>
	private sealed class PlainHttpServer : IDisposable
	{
		private readonly TcpListener _listener;
		private readonly CancellationTokenSource _cts = new();

		private PlainHttpServer(TcpListener listener)
		{
			_listener = listener;
		}

		public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

		public static PlainHttpServer Start()
		{
			TcpListener listener = new(IPAddress.Loopback, 0);
			listener.Start();
			PlainHttpServer server = new(listener);
			_ = server.AcceptLoopAsync();
			return server;
		}

		private async Task AcceptLoopAsync()
		{
			while (!_cts.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await _listener.AcceptTcpClientAsync(_cts.Token);
				}
				catch
				{
					return;
				}

				_ = HandleAsync(client);
			}
		}

		private async Task HandleAsync(TcpClient client)
		{
			using (client)
			{
				try
				{
					NetworkStream stream = client.GetStream();
					using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
					string? line;
					while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
					{
					}

					byte[] response = Encoding.ASCII.GetBytes(
						"HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
					await stream.WriteAsync(response, _cts.Token);
					await stream.FlushAsync(_cts.Token);
				}
				catch
				{
					// Best-effort — the test only needs the client to observe SOME HTTP answer.
				}
			}
		}

		public void Dispose()
		{
			_cts.Cancel();
			_listener.Stop();
			_cts.Dispose();
		}
	}
}
