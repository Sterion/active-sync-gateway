using System.Net;
using System.Net.Sockets;
using System.Text;
using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   G9: <c>ImapMailBackend.UpdateItemAsync</c>'s Drafts content-rewrite is APPEND-then-DELETE
///   (append the merged draft, then flag+expunge the original). A fault landing between the two
///   leaves BOTH the original and the freshly-appended copy in the mailbox, under the ORIGINAL's
///   still-valid item key — so a client retry (using that same stale key, exactly the "snapshot
///   rollback" retry design AGENTS.md describes) re-executes the whole rewrite and appends a
///   SECOND stray, compounding rather than converging. A fake IMAP server drives this
///   deterministically: it closes the connection right after acknowledging the APPEND (so the
///   client never gets to send the STORE/EXPUNGE that would remove the original) — the exact wire
///   window the finding describes — then serves a normal second connection for the client's retry.
/// </summary>
public sealed class ImapDraftUpdateOrderingTests
{
	private static ImapOptions Options(int port) => new() { Host = "127.0.0.1", Port = port, UseSsl = false, Security = "None" };

	private static readonly BackendCredentials Credentials = new("user@example.test", "pw");

	[Fact]
	public async Task UpdateItemAsync_InterruptedAfterAppend_ThenRetried_ConvergesToOneDraft()
	{
		await using FakeImapServer server = new();
		CancellationToken ct = CancellationToken.None;
		System.Xml.Linq.XElement contentChange = new("ApplicationData",
			new System.Xml.Linq.XElement(EasNamespaces.Email + "Subject", "g9-edited-subject"));
		string folderKey = ImapSession.ToBackendKey("Drafts");
		string itemKey = "1000:1"; // the pre-seeded original, uid 1

		bool firstAttemptFailed;
		await using (ImapSession session1 = new(Options(server.Port), Credentials, NullLogger.Instance))
		{
			ImapMailBackend backend1 = new(session1, null, _ => null, NullLogger.Instance);
			try
			{
				await backend1.UpdateItemAsync(folderKey, itemKey, contentChange, ct);
				firstAttemptFailed = false;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// Exactly the finding's own description: "an IOException after the APPEND lands
				// surfaces to the client" -- the connection the fake server closed right after
				// APPEND makes the following STORE/EXPUNGE (or, once fixed, the following APPEND)
				// fail with a transport-level exception, not a clean "not connected".
				firstAttemptFailed = true;
			}
		}

		// A real client only resends the Sync Change when it believes the first attempt failed --
		// exactly `firstAttemptFailed`. Under the current append-then-delete order this branch
		// DOES run (the original survives the fault, so the same stale key is still valid); once
		// fixed (delete-then-append) the first attempt itself succeeds and this is skipped.
		if (firstAttemptFailed)
		{
			await using ImapSession session2 = new(Options(server.Port), Credentials, NullLogger.Instance);
			ImapMailBackend backend2 = new(session2, null, _ => null, NullLogger.Instance);
			await backend2.UpdateItemAsync(folderKey, itemKey, contentChange, ct);
		}

		// Exactly one draft must remain: the original, replaced by the edit -- never both the
		// original AND a stray from the interrupted attempt, and never two strays.
		Assert.Equal(1, server.LiveMessageCount);
	}

	/// <summary>
	///   A minimal IMAP server (greeting/LOGIN/SELECT/UID FETCH/APPEND/UID STORE/UID EXPUNGE) over
	///   a loopback TCP socket -- just enough for MailKit's <see cref="MailKit.Net.Imap.ImapClient" />
	///   to drive <c>ImapMailBackend.UpdateItemAsync</c>'s Drafts rewrite. Starts with one live
	///   message (uid 1, UIDVALIDITY 1000) and tracks live UIDs across connections. The FIRST
	///   accepted connection closes immediately after acknowledging an APPEND -- before the client
	///   can send anything else -- reproducing "a fault between append and delete" without relying
	///   on a timing race; every later connection behaves normally.
	/// </summary>
	private sealed class FakeImapServer : IAsyncDisposable
	{
		private static readonly byte[] CannedMessage =
			Encoding.ASCII.GetBytes("From: user@example.test\r\nSubject: original\r\n\r\nbody\r\n");

		private readonly TcpListener _listener;
		private readonly Task _acceptTask;
		private readonly List<Task> _connections = [];
		private readonly Lock _gate = new();
		private readonly HashSet<uint> _liveUids = [1];
		private uint _nextUid = 2;
		private int _connectionIndex = -1;
		private volatile bool _stopping;

		public FakeImapServer()
		{
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
			_acceptTask = Task.Run(AcceptLoopAsync);
		}

		public int Port { get; }

		public int LiveMessageCount
		{
			get
			{
				lock (_gate)
					return _liveUids.Count;
			}
		}

		private async Task AcceptLoopAsync()
		{
			try
			{
				while (!_stopping)
				{
					TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
					int index = Interlocked.Increment(ref _connectionIndex);
					lock (_connections)
						_connections.Add(Task.Run(() => HandleConnectionAsync(client, index)));
				}
			}
			catch (Exception) when (_stopping)
			{
			}
		}

		private async Task HandleConnectionAsync(TcpClient client, int connectionIndex)
		{
			using (client)
			await using (NetworkStream stream = client.GetStream())
			{
				try
				{
					await WriteLineAsync(stream, "* OK [CAPABILITY IMAP4rev1 UIDPLUS] fake-imap ready").ConfigureAwait(false);

					while (true)
					{
						string line;
						try
						{
							line = await ReceiveCommandAsync(stream).ConfigureAwait(false);
						}
						catch (IOException)
						{
							return;
						}

						string tag = line.Split(' ', 2)[0];
						string upper = line.ToUpperInvariant();

						if (upper.Contains(" LOGIN "))
						{
							await WriteLineAsync(stream, $"{tag} OK [CAPABILITY IMAP4rev1 UIDPLUS] Logged in").ConfigureAwait(false);
						}
						else if (upper.Contains(" LIST "))
						{
							// GetFolderAsync resolves the folder via LIST before SELECT; echo back
							// whatever mailbox name the client asked for so it always resolves.
							int lastSpace = line.LastIndexOf(' ');
							string mailbox = line[(lastSpace + 1)..].Trim('"');
							await WriteLineAsync(stream, $"* LIST (\\HasNoChildren) \"/\" \"{mailbox}\"").ConfigureAwait(false);
							await WriteLineAsync(stream, $"{tag} OK LIST completed").ConfigureAwait(false);
						}
						else if (upper.Contains("SELECT") || upper.Contains("EXAMINE"))
						{
							int count;
							lock (_gate)
								count = _liveUids.Count;
							await WriteLineAsync(stream, $"* {count} EXISTS").ConfigureAwait(false);
							await WriteLineAsync(stream, "* 0 RECENT").ConfigureAwait(false);
							await WriteLineAsync(stream, "* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)").ConfigureAwait(false);
							await WriteLineAsync(stream, "* OK [UIDVALIDITY 1000] UIDs valid").ConfigureAwait(false);
							await WriteLineAsync(stream, "* OK [UIDNEXT 999] Predicted next UID").ConfigureAwait(false);
							await WriteLineAsync(stream,
								"* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] Limited").ConfigureAwait(false);
							await WriteLineAsync(stream, $"{tag} OK [READ-WRITE] SELECT completed").ConfigureAwait(false);
						}
						else if (upper.Contains("UID FETCH"))
						{
							await WriteRawAsync(stream,
								$"* 1 FETCH (UID 1 BODY[] {{{CannedMessage.Length}}}\r\n").ConfigureAwait(false);
							await stream.WriteAsync(CannedMessage).ConfigureAwait(false);
							await WriteLineAsync(stream, ")").ConfigureAwait(false);
							await WriteLineAsync(stream, $"{tag} OK FETCH completed").ConfigureAwait(false);
						}
						else if (upper.Contains("APPEND"))
						{
							uint newUid;
							lock (_gate)
							{
								newUid = _nextUid++;
								_liveUids.Add(newUid);
							}

							await WriteLineAsync(stream, $"{tag} OK [APPENDUID 1000 {newUid}] APPEND completed")
								.ConfigureAwait(false);

							if (connectionIndex == 0)
								// The fault window this finding is about: the client has the APPEND's
								// success but nothing further -- it never gets to flag+expunge the
								// original (old order) or, once fixed, never even reaches this point
								// because delete now comes first.
								return;
						}
						else if (upper.Contains("UID STORE"))
						{
							await WriteLineAsync(stream, $"{tag} OK STORE completed").ConfigureAwait(false);
						}
						else if (upper.Contains("UID EXPUNGE"))
						{
							string[] parts = line.Split(' ');
							string uidToken = parts[^1];
							if (uint.TryParse(uidToken, out uint expungedUid))
							{
								lock (_gate)
									_liveUids.Remove(expungedUid);
								await WriteLineAsync(stream, "* 1 EXPUNGE").ConfigureAwait(false);
							}

							await WriteLineAsync(stream, $"{tag} OK UID EXPUNGE completed").ConfigureAwait(false);
						}
						else if (upper.Contains("LOGOUT"))
						{
							await WriteLineAsync(stream, "* BYE logging out").ConfigureAwait(false);
							await WriteLineAsync(stream, $"{tag} OK LOGOUT completed").ConfigureAwait(false);
							return;
						}
						else
						{
							await WriteLineAsync(stream, $"{tag} OK completed").ConfigureAwait(false);
						}
					}
				}
				catch (IOException)
				{
				}
				catch (ObjectDisposedException)
				{
				}
			}
		}

		private static async Task WriteLineAsync(NetworkStream stream, string text)
		{
			await WriteRawAsync(stream, text + "\r\n").ConfigureAwait(false);
		}

		private static async Task WriteRawAsync(NetworkStream stream, string text)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(text);
			await stream.WriteAsync(bytes).ConfigureAwait(false);
			await stream.FlushAsync().ConfigureAwait(false);
		}

		/// <summary>Reads one CRLF-terminated line (terminator stripped).</summary>
		private static async Task<string> ReceiveLineAsync(NetworkStream stream)
		{
			List<byte> bytes = new();
			byte[] one = new byte[1];
			while (true)
			{
				int n = await stream.ReadAsync(one).ConfigureAwait(false);
				if (n == 0)
					throw new IOException("The client closed the connection.");
				if (one[0] == (byte)'\n' && bytes.Count > 0 && bytes[^1] == (byte)'\r')
				{
					bytes.RemoveAt(bytes.Count - 1);
					break;
				}

				bytes.Add(one[0]);
			}

			return Encoding.ASCII.GetString(bytes.ToArray());
		}

		private static async Task<byte[]> ReceiveBytesAsync(NetworkStream stream, int count)
		{
			byte[] buffer = new byte[count];
			int read = 0;
			while (read < count)
			{
				int n = await stream.ReadAsync(buffer.AsMemory(read, count - read)).ConfigureAwait(false);
				if (n == 0)
					throw new IOException("The client closed the connection mid-literal.");
				read += n;
			}

			return buffer;
		}

		/// <summary>
		///   Reads one client command line. When it ends in a synchronizing literal specifier
		///   (<c>{n}</c>, e.g. APPEND's message body), sends the "+" continuation prompt RFC 3501
		///   requires before the client will send the octets, reads them, and folds them into the
		///   returned text.
		/// </summary>
		private static async Task<string> ReceiveCommandAsync(NetworkStream stream)
		{
			string line = await ReceiveLineAsync(stream).ConfigureAwait(false);
			if (!line.EndsWith('}'))
				return line;

			int open = line.LastIndexOf('{');
			if (open < 0)
				return line;
			string countText = line[(open + 1)..^1].TrimEnd('+');
			if (!int.TryParse(countText, out int length))
				return line;

			await WriteLineAsync(stream, "+ Ready for literal data").ConfigureAwait(false);
			byte[] payload = await ReceiveBytesAsync(stream, length).ConfigureAwait(false);
			await ReceiveLineAsync(stream).ConfigureAwait(false); // the blank terminator line
			return line + Encoding.ASCII.GetString(payload);
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
			}
		}
	}
}
