using System.Net;
using System.Net.Sockets;
using System.Text;
using ActiveSync.Backends.Smtp;
using ActiveSync.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   G4: the SMTP DATA phase (<see cref="SmtpSubmitBackend.SendAsync" />) must not observe the
///   caller's cancellation token once the message is durably accepted server-side — otherwise a
///   client that drops the connection right after the server accepts the final "." sees the send
///   reported as a (cancelled) failure, resends, and the recipient gets the mail twice. A fake SMTP
///   server drives this deterministically: it signals the test the instant it has read the whole
///   message (the "." terminator), which is exactly the window the finding describes, and only then
///   is the caller's token cancelled — reproducing "accepted server-side, but the client's own
///   request already aborted" without depending on any real timing race.
/// </summary>
public sealed class SmtpSendCancellationTests
{
	[Fact]
	public async Task SendAsync_CancelledAfterServerAcceptsTheMessage_StillCompletes()
	{
		await using FakeSmtpServer server = new();

		SmtpSubmitBackend backend = new(
			new SmtpOptions { Host = IPAddress.Loopback.ToString(), Port = server.Port, Security = "None" },
			new BackendCredentials("user@example.test", "pw"),
			"user@example.test",
			NullLogger.Instance);

		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("user@example.test"));
		message.To.Add(MailboxAddress.Parse("dest@example.test"));
		message.Subject = "G4";
		message.Body = new TextPart("plain") { Text = "cancel-after-accept" };
		using MemoryStream buffer = new();
		await message.WriteToAsync(buffer);

		using CancellationTokenSource cts = new();

		// The instant the fake server has durably received the whole message (the "." terminator),
		// cancel the SAME token the phone's aborted request would carry — simulating the client
		// dropping the connection after the server has already accepted the mail — and only then let
		// the server send its final "250 OK" (proving the message really was accepted).
		Task signal = server.DataReceived.ContinueWith(_ =>
		{
			cts.Cancel();
			server.AllowFinalResponse();
		}, TaskScheduler.Default);

		// This must complete normally: the message was accepted server-side, so the client's own
		// request abort must not turn that into a reported failure (which would make the caller
		// resend and duplicate the mail — the defect this finding is about).
		await backend.SendAsync(buffer.ToArray(), cts.Token);
		await signal;

		Assert.True(server.MessageAccepted);
	}

	/// <summary>
	///   A minimal SMTP server (EHLO/AUTH PLAIN/MAIL/RCPT/DATA/QUIT) over a loopback TCP socket —
	///   just enough for MailKit's <see cref="MailKit.Net.Smtp.SmtpClient" /> to submit a message
	///   without TLS. <see cref="DataReceived" /> completes the instant the "." terminator has been
	///   read (the message is durably in server hands); the final "250" response is withheld until
	///   <see cref="AllowFinalResponse" /> is called, so a test can inject the client-side
	///   cancellation into exactly that window.
	/// </summary>
	private sealed class FakeSmtpServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly Task _serverTask;
		private readonly TaskCompletionSource _dataReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _canRespond = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public FakeSmtpServer()
		{
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
			_serverTask = Task.Run(RunAsync);
		}

		public int Port { get; }

		/// <summary>Completes once the server has read the full DATA payload's "." terminator.</summary>
		public Task DataReceived => _dataReceived.Task;

		/// <summary>True once the server actually wrote the final "250" for the accepted message.</summary>
		public bool MessageAccepted { get; private set; }

		/// <summary>Releases the withheld final "250 OK" for the DATA phase.</summary>
		public void AllowFinalResponse()
		{
			_canRespond.TrySetResult();
		}

		private async Task RunAsync()
		{
			using TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
			await using NetworkStream stream = client.GetStream();
			using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
			await using StreamWriter writer =
				new(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

			await writer.WriteLineAsync("220 fake-smtp.test ESMTP").ConfigureAwait(false);

			string? line;
			while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
			{
				if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) ||
				    line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
				{
					await writer.WriteLineAsync("250-fake-smtp.test").ConfigureAwait(false);
					await writer.WriteLineAsync("250 AUTH PLAIN LOGIN").ConfigureAwait(false);
				}
				else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
				{
					// Tolerate both the initial-response form ("AUTH PLAIN <base64>") and the
					// continuation form ("AUTH PLAIN" alone, base64 on the following line).
					if (line.Trim().Equals("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
					{
						await writer.WriteLineAsync("334 ").ConfigureAwait(false);
						await reader.ReadLineAsync().ConfigureAwait(false);
					}
					await writer.WriteLineAsync("235 2.7.0 Authentication successful").ConfigureAwait(false);
				}
				else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
				{
					await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
				}
				else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
				{
					await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
				}
				else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
				{
					await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);

					string? dataLine;
					while ((dataLine = await reader.ReadLineAsync().ConfigureAwait(false)) is not null &&
					       dataLine != ".")
					{
					}

					// The message is now fully, durably received. Signal the test BEFORE responding —
					// this is the exact window G4 is about: accepted server-side, response not yet read.
					_dataReceived.TrySetResult();
					// VSTHRD003: awaiting this TaskCompletionSource on purpose — it is the test's own
					// signal (set by AllowFinalResponse), not foreign work, matching the same pattern
					// already accepted in DatabaseLogSinkTests.
#pragma warning disable VSTHRD003
					await _canRespond.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
					try
					{
						await writer.WriteLineAsync("250 2.0.0 OK id=fake").ConfigureAwait(false);
						MessageAccepted = true;
					}
					catch (IOException)
					{
						// The client already disconnected after cancelling — irrelevant to the test,
						// which only cares that the server tried to accept (MessageAccepted stays false
						// only if the write itself failed, which would indicate a real transport issue).
					}
				}
				else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
				{
					try
					{
						await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
					}
					catch (IOException)
					{
					}
					break;
				}
				else
				{
					await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
				}
			}
		}

		public async ValueTask DisposeAsync()
		{
			_listener.Stop();
			try
			{
				// VSTHRD003: awaiting the background accept/read loop kicked off by Task.Run in the
				// constructor is intentional — it is this test double's own connection handler, not
				// foreign work, and there is no ambient SynchronizationContext in xunit to deadlock on.
#pragma warning disable VSTHRD003
				await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch (Exception)
			{
				// Best-effort teardown only — a lingering accept/read on a stopped listener throwing
				// on cleanup must not fail the test that already asserted what it needed to.
			}
		}
	}
}
