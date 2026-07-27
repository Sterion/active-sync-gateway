using System.Net;
using System.Net.Sockets;
using System.Text;
using ActiveSync.Backends.Sieve;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Round 3, item 6 — ManageSieve protocol safety (<c>G1</c>, <c>G2</c>, <c>G5</c>, <c>G10</c>,
///   <c>G17</c>, <c>G23</c>, <c>G24</c>). Each test drives <see cref="ManageSieveClient" /> (and, for
///   <c>G23</c>, <see cref="SieveOofBackend" />) against <see cref="RawSieveServer" /> — a scripted
///   loopback TCP double with no RFC 5804 logic of its own, so each test can supply the exact
///   (including malformed/adversarial) wire bytes the finding describes.
/// </summary>
public sealed class ManageSieveClientTests
{
	private static SieveOptions Options(int port) => new() { Host = "127.0.0.1", Port = port, UseTls = false };

	private static readonly BackendCredentials Credentials = new("user@example.test", "pw");

	// G1 -----------------------------------------------------------------------------------

	/// <summary>
	///   G1: RFC 5804 §2.7 lets a LISTSCRIPTS name arrive as a literal, with " ACTIVE" trailing on the
	///   SAME physical line after the literal's raw octets. `ReadResponseAsync` folds the literal into a
	///   quoted name but drops that trailing text — Cyrus timsieved emits names as literals, so a real
	///   user's own scripts read back with `Active == false` for all of them.
	/// </summary>
	[Fact]
	public async Task ListScriptsAsync_LiteralEncodedName_KeepsTrailingActiveFlag()
	{
		await using RawSieveServer server = new();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		Task serverTask = Task.Run(async () =>
		{
			await server.AcceptAsync(cts.Token);
			await server.SendAsync("\"SASL\" \"PLAIN\"\r\nOK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // AUTHENTICATE
			await server.SendAsync("OK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // LISTSCRIPTS
			// "myscript.sv" as an 11-octet literal, with " ACTIVE" trailing on the same
			// physical line — exactly the wire shape RFC 5804 allows and Cyrus emits.
			await server.SendAsync("{11}\r\nmyscript.sv ACTIVE\r\nOK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // LOGOUT
			await server.SendAsync("OK\r\n", cts.Token);
		});

		ManageSieveClient client = new(Options(server.Port), Credentials);
		await client.ConnectAsync(cts.Token);

		IReadOnlyList<(string Name, bool Active)> scripts = await client.ListScriptsAsync(cts.Token);

		await client.DisposeAsync();
		await serverTask;

		(string Name, bool Active) script = Assert.Single(scripts);
		Assert.Equal("myscript.sv", script.Name);
		Assert.True(script.Active, "the ACTIVE flag trailing a literal-encoded name must survive folding");
	}

	// G2 -----------------------------------------------------------------------------------

	/// <summary>
	///   G2: a server-controlled literal length is used directly as an allocation size with no
	///   ceiling. A hostile/misbehaving server can advertise an enormous length and the client must
	///   reject it outright — not allocate a buffer for it and then block waiting for bytes that never
	///   arrive.
	/// </summary>
	[Fact]
	public async Task ListScriptsAsync_OversizedServerLiteral_IsRejectedWithoutAttemptingToReadIt()
	{
		await using RawSieveServer server = new();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		Task serverTask = Task.Run(async () =>
		{
			await server.AcceptAsync(cts.Token);
			await server.SendAsync("\"SASL\" \"PLAIN\"\r\nOK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // AUTHENTICATE
			await server.SendAsync("OK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // LISTSCRIPTS
			// Advertises a 2,000,000-octet literal (well above any sane script-name size, and
			// above the intended ceiling) and then never sends that data — a hostile server.
			await server.SendAsync("{2000000+}\r\n", cts.Token);
		});

		ManageSieveClient client = new(Options(server.Port), Credentials);
		await client.ConnectAsync(cts.Token);

		// Bounded independently of the client's own guard: if the client has no ceiling it will
		// try to read the (never-arriving) 2,000,000 octets and this token fires first, throwing
		// OperationCanceledException — the wrong exception type, proving no ceiling exists.
		using CancellationTokenSource readCts = new(TimeSpan.FromSeconds(3));
		BackendException ex = await Assert.ThrowsAsync<BackendException>(
			() => client.ListScriptsAsync(readCts.Token));
		Assert.Contains("literal", ex.Message, StringComparison.OrdinalIgnoreCase);

		await serverTask;
	}

	// G5 -----------------------------------------------------------------------------------

	/// <summary>
	///   G5: `DisposeAsync`'s goodbye LOGOUT round trip uses `CancellationToken.None`, so a half-dead
	///   sieve server that accepts LOGOUT and never answers leaves disposal — and the caller's
	///   `await using` — pending forever. Proven by a fake server that reads LOGOUT and then goes
	///   silent; disposal must still complete within a short bound.
	/// </summary>
	[Fact]
	public async Task DisposeAsync_ServerNeverAnswersLogout_CompletesWithoutHanging()
	{
		await using RawSieveServer server = new();
		using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(15));

		Task serverTask = Task.Run(async () =>
		{
			await server.AcceptAsync(serverCts.Token);
			await server.SendAsync("\"SASL\" \"PLAIN\"\r\nOK\r\n", serverCts.Token);
			await server.ReceiveCommandAsync(serverCts.Token); // AUTHENTICATE
			await server.SendAsync("OK\r\n", serverCts.Token);
			await server.ReceiveCommandAsync(serverCts.Token); // LOGOUT — deliberately never answered
			// Stay connected but silent until the test's own server disposal severs the socket.
			await Task.Delay(Timeout.Infinite, serverCts.Token).ConfigureAwait(false);
		});

		using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(10));
		ManageSieveClient client = new(Options(server.Port), Credentials);
		await client.ConnectAsync(connectCts.Token);

		Task disposeTask = client.DisposeAsync().AsTask();
		Task winner = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));

		Assert.True(ReferenceEquals(disposeTask, winner),
			"DisposeAsync must not hang forever waiting on a server that never answers LOGOUT");

		await serverCts.CancelAsync();
		try
		{
			await serverTask;
		}
		catch (OperationCanceledException)
		{
			// expected teardown once the test cancels the background server loop
		}
	}

	// G10 ----------------------------------------------------------------------------------

	/// <summary>
	///   G10: `AUTHENTICATE "PLAIN"` is sent unconditionally, never checked against the server's
	///   advertised `"SASL"` capability line. A server that advertises SASL mechanisms excluding PLAIN
	///   (or an empty list) must be refused locally, with the client never putting the plaintext
	///   credentials on the wire at all.
	/// </summary>
	[Fact]
	public async Task ConnectAsync_ServerDoesNotAdvertisePlainSasl_RefusesLocally_WithoutSendingCredentials()
	{
		await using RawSieveServer server = new();
		using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(4));
		TaskCompletionSource<string?> sentAfterGreeting =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task serverTask = Task.Run(async () =>
		{
			await server.AcceptAsync(serverCts.Token);
			// Advertises SASL, but only GSSAPI — PLAIN is not offered.
			await server.SendAsync("\"SASL\" \"GSSAPI\"\r\nOK\r\n", serverCts.Token);
			try
			{
				string line = await server.ReceiveCommandAsync(serverCts.Token);
				sentAfterGreeting.TrySetResult(line);
				// Answer it (without mentioning SASL in the text) so an unfixed client's
				// ConnectAsync does not hang waiting for a response.
				await server.SendAsync("NO authentication rejected\r\n", serverCts.Token);
			}
			catch (OperationCanceledException)
			{
				// Expected once the fix lands: the client never sends AUTHENTICATE, so this read
				// never completes and the server-side deadline fires instead.
				sentAfterGreeting.TrySetResult(null);
			}
		});

		using CancellationTokenSource clientCts = new(TimeSpan.FromSeconds(6));
		ManageSieveClient client = new(Options(server.Port), Credentials);

		BackendException ex = await Assert.ThrowsAsync<BackendException>(() => client.ConnectAsync(clientCts.Token));
		Assert.Contains("SASL", ex.Message, StringComparison.OrdinalIgnoreCase);

		string? sentLine = await sentAfterGreeting.Task;
		Assert.Null(sentLine); // the fixed client must never put credentials on the wire here

		await serverTask;
	}

	// G17 ----------------------------------------------------------------------------------

	/// <summary>
	///   G17: <see cref="ManageSieveClient.Quote" /> escapes only backslash and double-quote — never
	///   control characters — even though RFC 5804's <c>quoted-string</c> forbids them. A script name
	///   containing a raw CR/LF (arriving as a literal, later re-quoted into a SETACTIVE/DELETESCRIPT
	///   command) therefore injects a line break into the command stream.
	/// </summary>
	[Fact]
	public void Quote_ValueContainingControlCharacters_NeutralizesThem()
	{
		string quoted = ManageSieveClient.Quote("evil\r\nDELETESCRIPT \"other\"");

		Assert.DoesNotContain('\r', quoted);
		Assert.DoesNotContain('\n', quoted);
	}

	/// <summary>
	///   G17 (folding side): a literal-encoded name containing a bare line feed — not a CRLF pair —
	///   must not survive into the parsed script name as a raw control character; only the CRLF-pair
	///   case is normalized today.
	/// </summary>
	[Fact]
	public async Task ListScriptsAsync_LiteralNameWithLoneLineFeed_DoesNotEmbedRawControlCharacters()
	{
		await using RawSieveServer server = new();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		Task serverTask = Task.Run(async () =>
		{
			await server.AcceptAsync(cts.Token);
			await server.SendAsync("\"SASL\" \"PLAIN\"\r\nOK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // AUTHENTICATE
			await server.SendAsync("OK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // LISTSCRIPTS
			// "abc\ndef" as a 7-octet literal (a lone LF, not a CRLF pair) followed by " ACTIVE".
			await server.SendAsync("{7}\r\nabc\ndef ACTIVE\r\nOK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // LOGOUT
			await server.SendAsync("OK\r\n", cts.Token);
		});

		ManageSieveClient client = new(Options(server.Port), Credentials);
		await client.ConnectAsync(cts.Token);
		IReadOnlyList<(string Name, bool Active)> scripts = await client.ListScriptsAsync(cts.Token);
		await client.DisposeAsync();
		await serverTask;

		(string Name, bool Active) script = Assert.Single(scripts);
		Assert.DoesNotContain('\n', script.Name);
		Assert.DoesNotContain('\r', script.Name);
	}

	// G23 ----------------------------------------------------------------------------------

	/// <summary>
	///   G23: when SETACTIVE is refused after PUTSCRIPT already landed, <see cref="SieveOofBackend" />
	///   propagates the failure but never cleans up — the gateway's own vacation script is orphaned on
	///   the user's sieve server forever, since <c>DisableAsync</c> only runs when the DB row says Oof
	///   was actually armed.
	/// </summary>
	[Fact]
	public async Task EnableAsync_SetActiveRefused_DeletesTheOrphanedScript_ThenRethrows()
	{
		await using RawSieveServer server = new();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		List<string> commands = new();

		Task serverTask = Task.Run(async () =>
		{
			await server.AcceptAsync(cts.Token);
			await server.SendAsync("\"SASL\" \"PLAIN\"\r\nOK\r\n", cts.Token);
			commands.Add(await server.ReceiveCommandAsync(cts.Token)); // AUTHENTICATE
			await server.SendAsync("OK\r\n", cts.Token);
			commands.Add(await server.ReceiveCommandAsync(cts.Token)); // LISTSCRIPTS
			await server.SendAsync("OK\r\n", cts.Token); // no existing scripts
			commands.Add(await server.ReceiveCommandAsync(cts.Token)); // PUTSCRIPT "eas-gateway" {n+}<script>
			await server.SendAsync("OK\r\n", cts.Token);
			commands.Add(await server.ReceiveCommandAsync(cts.Token)); // SETACTIVE "eas-gateway"
			await server.SendAsync("NO quota exceeded\r\n", cts.Token);
			// The fix must clean up the orphan before rethrowing.
			commands.Add(await server.ReceiveCommandAsync(cts.Token));
			await server.SendAsync("OK\r\n", cts.Token);
			commands.Add(await server.ReceiveCommandAsync(cts.Token)); // LOGOUT
			await server.SendAsync("OK\r\n", cts.Token);
		});

		SieveOofBackend backend = new(Options(server.Port), Credentials);
		OofReply reply = new("Away.", false, null, null);

		BackendException ex = await Assert.ThrowsAsync<BackendException>(() => backend.EnableAsync(reply, cts.Token));
		Assert.Contains("SETACTIVE", ex.Message, StringComparison.OrdinalIgnoreCase);

		await serverTask;

		Assert.Contains(commands, c => c.StartsWith("DELETESCRIPT", StringComparison.OrdinalIgnoreCase));
	}

	// G24 ----------------------------------------------------------------------------------

	/// <summary>
	///   G24 (coverage, not proof — see the test body): the <c>open >= 0</c> guard runs AFTER the slice
	///   it is meant to protect. Today that slice happens to stay in range whenever the line ends with
	///   '}' (the minimum such line, "}", has length 1, so `line[0..^1]` is always a valid — possibly
	///   empty — range), so no input reproduces a crash on unmodified code; the finding itself says so
	///   ("it does not throw today"). This is filed as coverage for the corrected order, not a red-first
	///   reproduction: it asserts a hostile line with no '{' at all is parsed as plain text (no literal
	///   consumed, no exception), which holds both before and after the fix and guards the fixed
	///   ordering against a future edit that would otherwise throw ArgumentOutOfRangeException.
	/// </summary>
	[Fact]
	public async Task ListScriptsAsync_LineEndingInBraceWithNoOpeningBrace_IsTreatedAsPlainText()
	{
		await using RawSieveServer server = new();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		Task serverTask = Task.Run(async () =>
		{
			await server.AcceptAsync(cts.Token);
			await server.SendAsync("\"SASL\" \"PLAIN\"\r\nOK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // AUTHENTICATE
			await server.SendAsync("OK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // LISTSCRIPTS
			// A hostile/malformed data line ending in '}' with no '{' anywhere — no literal to
			// consume, just plain (if odd) text.
			await server.SendAsync("\"weird}\"\r\nOK\r\n", cts.Token);
			await server.ReceiveCommandAsync(cts.Token); // LOGOUT
			await server.SendAsync("OK\r\n", cts.Token);
		});

		ManageSieveClient client = new(Options(server.Port), Credentials);
		await client.ConnectAsync(cts.Token);
		IReadOnlyList<(string Name, bool Active)> scripts = await client.ListScriptsAsync(cts.Token);
		await client.DisposeAsync();
		await serverTask;

		(string Name, bool Active) script = Assert.Single(scripts);
		Assert.Equal("weird}", script.Name);
	}

	/// <summary>
	///   Scripted raw-socket ManageSieve server double: gives each test full control over the exact
	///   wire bytes sent to <see cref="ManageSieveClient" />, including malformed/adversarial framing —
	///   no RFC 5804 logic of its own.
	/// </summary>
	private sealed class RawSieveServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private TcpClient? _client;
		private NetworkStream? _stream;

		public RawSieveServer()
		{
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		}

		public int Port { get; }

		public async Task AcceptAsync(CancellationToken ct)
		{
			_client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
			_stream = _client.GetStream();
		}

		/// <summary>Writes the given raw text (caller supplies CRLFs) verbatim to the client.</summary>
		public async Task SendAsync(string raw, CancellationToken ct)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(raw);
			await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
			await _stream.FlushAsync(ct).ConfigureAwait(false);
		}

		/// <summary>Reads one CRLF-terminated line (terminator stripped).</summary>
		public async Task<string> ReceiveLineAsync(CancellationToken ct)
		{
			List<byte> bytes = new();
			byte[] one = new byte[1];
			while (true)
			{
				int n = await _stream!.ReadAsync(one, ct).ConfigureAwait(false);
				if (n == 0)
					throw new IOException("The client closed the connection.");
				if (one[0] == (byte)'\n' && bytes.Count > 0 && bytes[^1] == (byte)'\r')
				{
					bytes.RemoveAt(bytes.Count - 1);
					break;
				}

				bytes.Add(one[0]);
			}

			return Encoding.UTF8.GetString(bytes.ToArray());
		}

		private async Task<byte[]> ReceiveBytesAsync(int count, CancellationToken ct)
		{
			byte[] buffer = new byte[count];
			int read = 0;
			while (read < count)
			{
				int n = await _stream!.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
				if (n == 0)
					throw new IOException("The client closed the connection mid-literal.");
				read += n;
			}

			return buffer;
		}

		/// <summary>
		///   Reads one client command line. When it ends in a literal specifier (<c>{n}</c> or
		///   <c>{n+}</c> — PUTSCRIPT's script body), also consumes the following <paramref name="n" />
		///   octets plus the blank terminator line, and folds them into the returned text so callers can
		///   still assert on the command verb.
		/// </summary>
		public async Task<string> ReceiveCommandAsync(CancellationToken ct)
		{
			string line = await ReceiveLineAsync(ct).ConfigureAwait(false);
			if (!line.EndsWith('}'))
				return line;

			int open = line.LastIndexOf('{');
			if (open < 0)
				return line;
			string countText = line[(open + 1)..^1].TrimEnd('+');
			if (!int.TryParse(countText, out int length))
				return line;

			byte[] payload = await ReceiveBytesAsync(length, ct).ConfigureAwait(false);
			await ReceiveLineAsync(ct).ConfigureAwait(false); // the blank terminator line
			return line + Encoding.UTF8.GetString(payload);
		}

		public ValueTask DisposeAsync()
		{
			_stream?.Dispose();
			_client?.Dispose();
			_listener.Stop();
			return ValueTask.CompletedTask;
		}
	}
}
