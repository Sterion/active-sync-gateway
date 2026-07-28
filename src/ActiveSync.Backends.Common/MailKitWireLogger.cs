using System.Text;
using ActiveSync.Protocol;
using MailKit;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Common;

/// <summary>
///   MailKit <see cref="IProtocolLogger" /> that forwards the raw IMAP/SMTP exchange to an
///   <see cref="ILogger" /> at Trace, one protocol line per event ("C:" client, "S:" server),
///   tagged with a short connection id so concurrent connections stay readable. Credentials
///   are redacted through MailKit's own mechanism: the client assigns
///   <see cref="AuthenticationSecretDetector" /> while authenticating, and every byte range it
///   reports is masked before the line is logged (the same contract MailKit's built-in
///   ProtocolLogger uses for RedactSecrets — covers LOGIN, AUTH/AUTHENTICATE and every SASL
///   round-trip without protocol-specific guessing).
/// </summary>
public sealed class MailKitWireLogger(ILogger logger) : IProtocolLogger
{
	private readonly string _connectionId = Guid.NewGuid().ToString("N")[..6];
	private readonly StringBuilder _clientBuffer = new();
	private readonly StringBuilder _serverBuffer = new();

	public IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }

	public void LogConnect(Uri uri)
	{
		logger.LogTrace("[{ConnectionId}] Connected to {Uri}", _connectionId, uri);
	}

	public void LogClient(byte[] buffer, int offset, int count)
	{
		Append(_clientBuffer, "C", Redact(buffer, offset, count));
	}

	public void LogServer(byte[] buffer, int offset, int count)
	{
		Append(_serverBuffer, "S", Latin1(buffer, offset, count));
	}

	public void Dispose()
	{
		Flush(_clientBuffer, "C");
		Flush(_serverBuffer, "S");
	}

	/// <summary>Masks every secret byte range MailKit's active detector reports.</summary>
	private string Redact(byte[] buffer, int offset, int count)
	{
		IList<AuthenticationSecret>? secrets = AuthenticationSecretDetector?.DetectSecrets(buffer, offset, count);
		if (secrets is not { Count: > 0 })
			return Latin1(buffer, offset, count);

		StringBuilder masked = new(count);
		int index = offset;
		foreach (AuthenticationSecret secret in secrets)
		{
			if (secret.StartIndex > index)
				masked.Append(Latin1(buffer, index, secret.StartIndex - index));
			masked.Append("********");
			index = secret.StartIndex + secret.Length;
		}

		if (index < offset + count)
			masked.Append(Latin1(buffer, index, offset + count - index));
		return masked.ToString();
	}

	// MailKit hands arbitrary chunks; accumulate per direction and emit whole lines.
	// D28: scan the StringBuilder's own indexer for '\n' instead of calling pending.ToString()
	// (twice) per line found -- the old code re-stringified the WHOLE remaining buffer on every
	// iteration, so a chunk of N lines allocated on the order of N copies of the (shrinking)
	// buffer, i.e. roughly O(N^2) bytes total rather than O(N). A large FETCH response delivered
	// across many chunks, each with several lines, amplified allocation by the lines-per-chunk
	// factor -- exactly when Trace logging is already enabled to debug a slow mailbox.
	private void Append(StringBuilder pending, string direction, string text)
	{
		pending.Append(text);
		while (true)
		{
			int newline = -1;
			for (int i = 0; i < pending.Length; i++)
			{
				if (pending[i] != '\n')
					continue;
				newline = i;
				break;
			}

			if (newline < 0)
				return;
			string line = pending.ToString(0, newline).TrimEnd('\r');
			pending.Remove(0, newline + 1);
			Emit(direction, line);
		}
	}

	private void Flush(StringBuilder pending, string direction)
	{
		if (pending.Length == 0)
			return;
		Emit(direction, pending.ToString());
		pending.Clear();
	}

	private void Emit(string direction, string line)
	{
		logger.LogTrace("[{ConnectionId}] {Direction}: {Line}",
			_connectionId, direction, WireLog.Payload(line, 4096));
	}

	// Protocol text is ASCII; literals may carry arbitrary bytes — Latin-1 maps every byte
	// to a char without throwing, and WireLog.Payload neutralizes control characters.
	private static string Latin1(byte[] buffer, int offset, int count)
	{
		return Encoding.Latin1.GetString(buffer, offset, count);
	}
}
