using System.Text;
using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using MailKit;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The Verbose wire tier: MailKit protocol chunks reassemble into whole log lines, and the
///   byte ranges MailKit's authentication secret detector reports never reach the log.
/// </summary>
public sealed class MailKitWireLoggerTests
{
	private static (MailKitWireLogger Logger, List<string> Lines) Create()
	{
		CollectingLogger collector = new();
		return (new MailKitWireLogger(collector), collector.Lines);
	}

	private static void Client(MailKitWireLogger logger, string text)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(text);
		logger.LogClient(bytes, 0, bytes.Length);
	}

	[Fact]
	public void ChunkedWrites_ReassembleIntoWholeLines()
	{
		(MailKitWireLogger logger, List<string> lines) = Create();
		byte[] chunk = Encoding.ASCII.GetBytes("* OK Stal");
		logger.LogServer(chunk, 0, chunk.Length);
		Assert.Empty(lines);

		chunk = Encoding.ASCII.GetBytes("wart ready\r\nA1 OK\r\n");
		logger.LogServer(chunk, 0, chunk.Length);
		Assert.Equal(2, lines.Count);
		Assert.EndsWith("S: * OK Stalwart ready", lines[0]);
		Assert.EndsWith("S: A1 OK", lines[1]);
	}

	[Fact]
	public void DetectedSecrets_AreMasked()
	{
		(MailKitWireLogger logger, List<string> lines) = Create();
		const string command = "A1 LOGIN user1@example.com hunter2\r\n";
		logger.AuthenticationSecretDetector = new FixedRangeDetector(command, "hunter2");

		Client(logger, command);
		string line = Assert.Single(lines);
		Assert.Contains("A1 LOGIN user1@example.com ********", line);
		Assert.DoesNotContain("hunter2", line);
	}

	[Fact]
	public void WithoutDetector_LinesPassThrough()
	{
		(MailKitWireLogger logger, List<string> lines) = Create();
		Client(logger, "A2 SELECT INBOX\r\n");
		Assert.Contains("C: A2 SELECT INBOX", Assert.Single(lines));
	}

	[Fact]
	public void Dispose_FlushesAPartialLine()
	{
		(MailKitWireLogger logger, List<string> lines) = Create();
		Client(logger, "A3 LOGOUT"); // no newline
		Assert.Empty(lines);
		logger.Dispose();
		Assert.Contains("C: A3 LOGOUT", Assert.Single(lines));
	}

	// D28: Append re-stringified its WHOLE pending buffer via pending.ToString() twice per line
	// found, inside the loop over every line in the chunk -- so a chunk of N lines allocated on
	// the order of N copies of the (shrinking) remaining buffer, i.e. roughly O(N^2) total bytes
	// rather than O(N). Only reached when Trace is enabled (exactly when someone is debugging a
	// slow mailbox), so this is precisely the case a large FETCH response should not amplify.
	// A single large multi-line chunk makes the O(N^2) vs O(N) gap large enough (100x+) to assert
	// on deterministically without depending on GC timing or a close race.
	[Fact]
	public void LargeChunk_DoesNotReallocateTheWholeBufferPerLine()
	{
		(MailKitWireLogger logger, List<string> lines) = Create();
		const int lineCount = 1000;
		const string lineBody = "* 12345 FETCH (UID 12345 FLAGS (\\Seen) RFC822.SIZE 4096)";
		byte[] chunk = Encoding.ASCII.GetBytes(
			string.Concat(Enumerable.Repeat(lineBody + "\r\n", lineCount)));

		long before = GC.GetAllocatedBytesForCurrentThread();
		logger.LogServer(chunk, 0, chunk.Length);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.Equal(lineCount, lines.Count);
		// O(N) processing of ~60 KB of input allocates low-single-digit MB at most (line buffers,
		// log-line formatting); the O(N^2) defect allocates on the order of 100+ MB for the same
		// input. 10 MB is a wide margin above the former and far below the latter.
		Assert.True(allocated < 10 * 1024 * 1024,
			$"expected roughly linear allocation, got {allocated:N0} bytes for {lineCount} lines");
	}

	[Fact]
	public void OversizedLine_IsTruncated()
	{
		(MailKitWireLogger logger, List<string> lines) = Create();
		Client(logger, new string('x', 10_000) + "\r\n");
		Assert.Contains("[truncated, 10000 chars total]", Assert.Single(lines));
	}

	/// <summary>Stands in for the detector MailKit's clients assign during authentication.</summary>
	private sealed class FixedRangeDetector(string command, string secret) : IAuthenticationSecretDetector
	{
		public IList<AuthenticationSecret> DetectSecrets(byte[] buffer, int offset, int count)
		{
			int index = command.IndexOf(secret, StringComparison.Ordinal);
			return index >= 0 && index + secret.Length <= offset + count
				? [new AuthenticationSecret(offset + index, secret.Length)]
				: [];
		}
	}

	private sealed class CollectingLogger : ILogger
	{
		public List<string> Lines { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
			Exception? exception, Func<TState, Exception?, string> formatter)
		{
			Lines.Add(formatter(state, exception));
		}
	}
}
