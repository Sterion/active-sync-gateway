using System.Net;
using System.Net.Sockets;
using System.Text;
using ActiveSync.Backends.Smtp;
using ActiveSync.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The RFC 1870 SIZE preflight measured <c>mime.LongLength</c> — the caller-supplied bytes
///   BEFORE <see cref="SmtpOptions.ForceFrom" /> rewrites the <c>From</c> header — not what
///   <c>SmtpClient.SendAsync</c> actually transmits (it re-serializes the, possibly mutated,
///   <see cref="MimeMessage" />). Reproduced deterministically against a fake SMTP server that
///   advertises a SIZE limit: the original bytes carry a short From address comfortably under the
///   limit, but <c>ForceFrom</c> rewrites it to a much longer canonical address, pushing the
///   RE-serialized message over the limit — the preflight must catch that, not let a message it
///   never actually measured reach the wire.
/// </summary>
public sealed class SmtpSizePreflightMeasurementTests
{
	[Fact]
	public async Task SendAsync_ForceFromGrowsTheMessage_PreflightMeasuresTheRewrittenSize_NotTheOriginalBytes()
	{
		string longCanonicalAddress = new string('a', 200) + "@example.test";

		MimeMessage original = new();
		original.From.Add(MailboxAddress.Parse("a@b.co")); // short -- ForceFrom replaces this
		original.To.Add(MailboxAddress.Parse("dest@example.test"));
		original.Subject = "G29";
		original.Body = new TextPart("plain") { Text = "small" };
		using MemoryStream buffer = new();
		await original.WriteToAsync(buffer);
		byte[] mime = buffer.ToArray();

		// Comfortably over the ORIGINAL bytes' length, under what the REWRITTEN message will serialize to.
		await using FakeSizeAdvertisingSmtpServer server =
			FakeSizeAdvertisingSmtpServer.StartWithMaxSize((uint)mime.Length + 50);

		SmtpOptions options = new()
		{
			Host = IPAddress.Loopback.ToString(), Port = server.Port, Security = "None", ForceFrom = true
		};
		SmtpSubmitBackend backend = new(
			options, new BackendCredentials("user@example.test", "pw"), longCanonicalAddress, NullLogger.Instance);

		await Assert.ThrowsAsync<BackendException>(() => backend.SendAsync(mime, CancellationToken.None));
	}

	/// <summary>
	///   A minimal SMTP server (EHLO/AUTH PLAIN/MAIL/RCPT/DATA/QUIT) over a loopback TCP socket that
	///   additionally advertises <c>SIZE &lt;MaxSize&gt;</c> at EHLO — just enough for MailKit's
	///   <see cref="MailKit.Net.Smtp.SmtpClient" /> to populate <c>Capabilities</c>/<c>MaxSize</c>
	///   and submit a message without TLS.
	/// </summary>
	private sealed class FakeSizeAdvertisingSmtpServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly Task _serverTask;
		private uint _maxSize;

		private FakeSizeAdvertisingSmtpServer(TcpListener listener, uint maxSize)
		{
			_listener = listener;
			_maxSize = maxSize;
			Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
			_serverTask = Task.Run(RunAsync);
		}

		public static FakeSizeAdvertisingSmtpServer StartWithMaxSize(uint maxSize)
		{
			TcpListener listener = new(IPAddress.Loopback, 0);
			listener.Start();
			return new FakeSizeAdvertisingSmtpServer(listener, maxSize);
		}

		public int Port { get; }

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
					await writer.WriteLineAsync($"250-SIZE {_maxSize}").ConfigureAwait(false);
					await writer.WriteLineAsync("250 AUTH PLAIN LOGIN").ConfigureAwait(false);
				}
				else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
				{
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

					await writer.WriteLineAsync("250 2.0.0 OK id=fake").ConfigureAwait(false);
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
					await writer.WriteLineAsync("500 unrecognized").ConfigureAwait(false);
				}
			}
		}

		public async ValueTask DisposeAsync()
		{
			_listener.Stop();
			try
			{
				// VSTHRD003: awaiting the background accept/read loop kicked off by Task.Run in the
				// constructor is intentional -- it is this test double's own connection handler, not
				// foreign work, and there is no ambient SynchronizationContext in xunit to deadlock on
				// (same accepted pattern as SmtpSendCancellationTests.FakeSmtpServer).
#pragma warning disable VSTHRD003
				await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch
			{
				// best effort
			}
		}
	}
}
