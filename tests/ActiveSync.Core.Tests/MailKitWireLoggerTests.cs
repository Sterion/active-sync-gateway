using System.Text;
using ActiveSync.Backends.Common;
using ActiveSync.Core.Logging;
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
