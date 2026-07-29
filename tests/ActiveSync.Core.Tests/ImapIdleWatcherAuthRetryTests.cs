using System.Net;
using System.Net.Sockets;
using System.Text;
using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   <see cref="ImapIdleWatcher.RunAsync" /> treats ANY <c>AuthenticationException</c> as a
///   permanent credential rejection and latches the watcher unavailable forever — but MailKit
///   raises that exception for any negative LOGIN/AUTHENTICATE reply, including a transient one
///   (Dovecot's "NO [UNAVAILABLE] Maximum number of connections from user+IP exceeded", which the
///   per-(user,folder) dedicated-connection design provokes). A fake IMAP server drives this
///   deterministically: the first LOGIN attempt is refused with exactly that transient wording,
///   the second succeeds — the watcher must still be usable afterward, not latched unavailable
///   after the very first rejection.
/// </summary>
public sealed class ImapIdleWatcherAuthRetryTests
{
	private static ImapOptions Options(int port) => new() { Host = "127.0.0.1", Port = port, UseSsl = false, Security = "None" };

	private static readonly BackendCredentials Credentials = new() { UserName = "user@example.test", Password = "pw" };

	[Fact]
	public async Task WaitForChangeAsync_TransientAuthFailureThenSuccess_DoesNotLatchUnavailable()
	{
		await using FakeImapServer server = new(failFirstLogins: 1);

		await using ImapIdleWatcher watcher = new(
			Options(server.Port), Credentials, "INBOX", NullLogger.Instance);

		// Long enough to span: attempt 1 (fails fast) -> backoff -> attempt 2 (succeeds) -> IDLE.
		// The watcher never latches _unavailable on a transient failure, so this must return
		// false (genuine timeout, nothing happened) rather than null (watcher gave up).
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
		bool? result = await watcher.WaitForChangeAsync(DateTime.UtcNow, TimeSpan.FromSeconds(12), cts.Token);

		Assert.False(result is null,
			"a single transient auth failure must not permanently latch the watcher unavailable");
		Assert.Equal(2, server.LoginAttempts);
	}

	/// <summary>
	///   A minimal IMAP server (greeting/CAPABILITY/LOGIN/SELECT/IDLE) over a loopback TCP socket —
	///   just enough for MailKit's <see cref="MailKit.Net.Imap.ImapClient" /> to authenticate,
	///   select a folder and IDLE without TLS. The first <paramref name="failFirstLogins" /> LOGIN
	///   attempts (across reconnects) are refused with Dovecot's connection-cap wording; every
	///   later one succeeds.
	/// </summary>
	private sealed class FakeImapServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly Task _acceptTask;
		private readonly List<Task> _connections = [];
		private readonly int _failFirstLogins;
		private int _loginAttempts;
		private volatile bool _stopping;

		public FakeImapServer(int failFirstLogins)
		{
			_failFirstLogins = failFirstLogins;
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
			_acceptTask = Task.Run(AcceptLoopAsync);
		}

		public int Port { get; }
		public int LoginAttempts => Volatile.Read(ref _loginAttempts);

		private async Task AcceptLoopAsync()
		{
			try
			{
				while (!_stopping)
				{
					TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
					lock (_connections)
						_connections.Add(Task.Run(() => HandleConnectionAsync(client)));
				}
			}
			catch (Exception) when (_stopping)
			{
				// listener stopped during shutdown
			}
		}

		private async Task HandleConnectionAsync(TcpClient client)
		{
			using (client)
			await using (NetworkStream stream = client.GetStream())
			using (StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true))
			await using (StreamWriter writer = new(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
			{
				try
				{
					await writer.WriteLineAsync(
						"* OK [CAPABILITY IMAP4rev1 IDLE UIDPLUS] fake-imap ready").ConfigureAwait(false);

					string? line;
					while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
					{
						string upper = line.ToUpperInvariant();
						string tag = line.Split(' ', 2)[0];

						if (upper.Contains("CAPABILITY"))
						{
							await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 IDLE UIDPLUS").ConfigureAwait(false);
							await writer.WriteLineAsync($"{tag} OK CAPABILITY completed").ConfigureAwait(false);
						}
						else if (upper.Contains(" LOGIN "))
						{
							int attempt = Interlocked.Increment(ref _loginAttempts);
							if (attempt <= _failFirstLogins)
							{
								await writer.WriteLineAsync(
									$"{tag} NO [UNAVAILABLE] Maximum number of connections from user+IP exceeded")
									.ConfigureAwait(false);
								// The client disconnects after a failed AUTHENTICATE — stop serving
								// this connection so the next attempt gets a fresh one.
								return;
							}

							await writer.WriteLineAsync(
								$"{tag} OK [CAPABILITY IMAP4rev1 IDLE UIDPLUS] Logged in").ConfigureAwait(false);
						}
						else if (upper.Contains("LIST "))
						{
							await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"INBOX\"").ConfigureAwait(false);
							await writer.WriteLineAsync($"{tag} OK LIST completed").ConfigureAwait(false);
						}
						else if (upper.Contains("SELECT") || upper.Contains("EXAMINE"))
						{
							await writer.WriteLineAsync("* 0 EXISTS").ConfigureAwait(false);
							await writer.WriteLineAsync("* 0 RECENT").ConfigureAwait(false);
							await writer.WriteLineAsync("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)").ConfigureAwait(false);
							await writer.WriteLineAsync("* OK [UIDVALIDITY 1000] UIDs valid").ConfigureAwait(false);
							await writer.WriteLineAsync("* OK [UIDNEXT 1] Predicted next UID").ConfigureAwait(false);
							await writer.WriteLineAsync(
								"* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] Limited")
								.ConfigureAwait(false);
							await writer.WriteLineAsync($"{tag} OK [READ-WRITE] SELECT completed").ConfigureAwait(false);
						}
						else if (upper.Contains("IDLE"))
						{
							await writer.WriteLineAsync("+ idling").ConfigureAwait(false);
							// Sit idling until DONE or disconnect — no folder events fire in this test.
							string? doneLine = await reader.ReadLineAsync().ConfigureAwait(false);
							if (doneLine is null)
								return;
							await writer.WriteLineAsync($"{tag} OK IDLE completed").ConfigureAwait(false);
						}
						else if (upper.Contains("LOGOUT"))
						{
							await writer.WriteLineAsync("* BYE logging out").ConfigureAwait(false);
							await writer.WriteLineAsync($"{tag} OK LOGOUT completed").ConfigureAwait(false);
							return;
						}
						else
						{
							await writer.WriteLineAsync($"{tag} OK completed").ConfigureAwait(false);
						}
					}
				}
				catch (IOException)
				{
					// client disconnected — nothing to do
				}
				catch (ObjectDisposedException)
				{
				}
			}
		}

		public async ValueTask DisposeAsync()
		{
			_stopping = true;
			_listener.Stop();
			List<Task> connections;
			lock (_connections)
				connections = [.. _connections];
			try
			{
#pragma warning disable VSTHRD003
				await Task.WhenAll([_acceptTask, .. connections]).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch (Exception)
			{
				// best-effort teardown only
			}
		}
	}
}
