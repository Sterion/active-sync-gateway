using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Sieve;

/// <summary>
///   Minimal ManageSieve (RFC 5804) client — just enough for the gateway's out-of-office
///   feature: STARTTLS, AUTHENTICATE PLAIN, LISTSCRIPTS, PUTSCRIPT, SETACTIVE, DELETESCRIPT.
///   One connection per operation batch (Oof changes are rare); no pooling on purpose.
///   Wire logging at Trace on the "ActiveSync.Backends.Sieve" category; the AUTHENTICATE
///   line is always masked.
/// </summary>
public sealed class ManageSieveClient : IAsyncDisposable
{
	private readonly SieveOptions _options;
	private readonly BackendCredentials _credentials;
	private readonly ILogger? _wireLogger;
	private TcpClient? _tcp;
	private Stream? _stream;
	private StreamReader? _reader;

	public ManageSieveClient(SieveOptions options, BackendCredentials credentials, ILogger? wireLogger = null)
	{
		_options = options;
		_credentials = credentials;
		_wireLogger = wireLogger is { } logger && logger.IsEnabled(LogLevel.Trace) ? wireLogger : null;
	}

	/// <summary>Connects, upgrades to TLS when configured, and authenticates.</summary>
	public async Task ConnectAsync(CancellationToken ct)
	{
		_tcp = new TcpClient();
		try
		{
			await _tcp.ConnectAsync(_options.Host ?? "", _options.Port, ct).ConfigureAwait(false);
		}
		catch (SocketException ex)
		{
			throw new BackendException(
				$"Cannot reach the ManageSieve server at {_options.Host}:{_options.Port}: {ex.Message}", ex);
		}

		_stream = _tcp.GetStream();
		BindReader();

		// Greeting: capability lines terminated by OK.
		SieveResponse greeting = await ReadResponseAsync(ct).ConfigureAwait(false);
		greeting.ThrowUnlessOk("greeting");

		if (_options.UseTls)
		{
			if (!greeting.Lines.Any(l => l.StartsWith("\"STARTTLS\"", StringComparison.OrdinalIgnoreCase)))
				throw new BackendException(
					$"The ManageSieve server at {_options.Host}:{_options.Port} does not offer STARTTLS " +
					"but Sieve:UseTls is enabled.");
			await SendLineAsync("STARTTLS", ct).ConfigureAwait(false);
			(await ReadResponseAsync(ct).ConfigureAwait(false)).ThrowUnlessOk("STARTTLS");

			SslStream ssl = new(_stream, false,
				ServerCertificateValidator.CreateCallback(
					_options.AllowInvalidCertificates, _options.CaCertificatePath));
			await ssl.AuthenticateAsClientAsync(
				new SslClientAuthenticationOptions { TargetHost = _options.Host },
				ct).ConfigureAwait(false);
			_stream = ssl;
			BindReader();
			// The server re-announces its capabilities after the TLS handshake.
			(await ReadResponseAsync(ct).ConfigureAwait(false)).ThrowUnlessOk("post-TLS capabilities");
		}

		string token = Convert.ToBase64String(
			Encoding.UTF8.GetBytes($"\0{_credentials.UserName}\0{_credentials.Password}"));
		await SendLineAsync($"AUTHENTICATE \"PLAIN\" \"{token}\"", ct, "AUTHENTICATE \"PLAIN\" ********")
			.ConfigureAwait(false);
		SieveResponse auth = await ReadResponseAsync(ct).ConfigureAwait(false);
		if (!auth.IsOk)
			throw new BackendException(
				$"ManageSieve authentication failed for {_credentials.UserName}: {auth.StatusLine}");
	}

	/// <summary>Script names with the active one flagged. Names are unquoted.</summary>
	public async Task<IReadOnlyList<(string Name, bool Active)>> ListScriptsAsync(CancellationToken ct)
	{
		await SendLineAsync("LISTSCRIPTS", ct).ConfigureAwait(false);
		SieveResponse response = await ReadResponseAsync(ct).ConfigureAwait(false);
		response.ThrowUnlessOk("LISTSCRIPTS");

		List<(string, bool)> scripts = new();
		foreach (string line in response.Lines)
		{
			if (!line.StartsWith('"'))
				continue;
			int closing = line.IndexOf('"', 1);
			if (closing < 0)
				continue;
			string name = Unescape(line[1..closing]);
			bool active = line[(closing + 1)..].TrimStart().StartsWith("ACTIVE", StringComparison.OrdinalIgnoreCase);
			scripts.Add((name, active));
		}

		return scripts;
	}

	public async Task PutScriptAsync(string name, string script, CancellationToken ct)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(script);
		// Non-synchronizing literal ({n+}): the script bytes follow immediately.
		await SendLineAsync($"PUTSCRIPT {Quote(name)} {{{bytes.Length}+}}", ct).ConfigureAwait(false);
		await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
		await SendLineAsync("", ct).ConfigureAwait(false);
		_wireLogger?.LogTrace("C: <script, {Bytes} bytes>", bytes.Length);
		SieveResponse response = await ReadResponseAsync(ct).ConfigureAwait(false);
		if (!response.IsOk)
			throw new BackendException($"PUTSCRIPT {name} was refused: {response.StatusLine}");
	}

	/// <summary>SETACTIVE; an empty name deactivates all scripts (RFC 5804 §2.8).</summary>
	public async Task SetActiveAsync(string name, CancellationToken ct)
	{
		await SendLineAsync($"SETACTIVE {Quote(name)}", ct).ConfigureAwait(false);
		SieveResponse response = await ReadResponseAsync(ct).ConfigureAwait(false);
		if (!response.IsOk)
			throw new BackendException($"SETACTIVE {name} was refused: {response.StatusLine}");
	}

	/// <summary>DELETESCRIPT; a missing script is not an error for our callers.</summary>
	public async Task DeleteScriptAsync(string name, CancellationToken ct)
	{
		await SendLineAsync($"DELETESCRIPT {Quote(name)}", ct).ConfigureAwait(false);
		await ReadResponseAsync(ct).ConfigureAwait(false); // NO (nonexistent) is fine
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			if (_stream is not null)
			{
				await SendLineAsync("LOGOUT", CancellationToken.None).ConfigureAwait(false);
				await ReadResponseAsync(CancellationToken.None).ConfigureAwait(false);
			}
		}
		catch (Exception)
		{
			// best-effort goodbye — the transport is going away either way
		}
		finally
		{
			_stream?.Dispose();
			_tcp?.Dispose();
		}
	}

	/// <summary>RFC 5804 quoted string: backslash-escape backslashes and double quotes.</summary>
	public static string Quote(string value)
	{
		return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
	}

	public static string Unescape(string value)
	{
		return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
	}

	private void BindReader()
	{
		// ASCII-transparent; script bytes are written to the stream directly, and response
		// lines are protocol text. leaveOpen: the stream is disposed once, by DisposeAsync.
		_reader = new StreamReader(_stream!, Encoding.UTF8, false, 1024, true);
	}

	private async Task SendLineAsync(string line, CancellationToken ct, string? logAs = null)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
		await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
		await _stream.FlushAsync(ct).ConfigureAwait(false);
		if (line.Length > 0)
			_wireLogger?.LogTrace("C: {Line}", logAs ?? line);
	}

	/// <summary>
	///   Reads data lines until the OK/NO/BYE status line. Literal markers ({n}) in data
	///   lines are consumed as raw bytes and folded into the preceding line, so a server
	///   sending script names as literals cannot desynchronize the reader.
	/// </summary>
	private async Task<SieveResponse> ReadResponseAsync(CancellationToken ct)
	{
		List<string> lines = new();
		while (true)
		{
			string? line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false)
				?? throw new BackendException("The ManageSieve server closed the connection unexpectedly.");
			_wireLogger?.LogTrace("S: {Line}", line);

			if (line.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
			    line.StartsWith("NO", StringComparison.OrdinalIgnoreCase) ||
			    line.StartsWith("BYE", StringComparison.OrdinalIgnoreCase))
				return new SieveResponse(line, lines);

			// Trailing synchronizing literal: consume its byte count from the reader.
			if (line.EndsWith('}'))
			{
				int open = line.LastIndexOf('{');
				string count = line[(open + 1)..^1].TrimEnd('+');
				if (open >= 0 && int.TryParse(count, out int literalLength) && literalLength >= 0)
				{
					char[] buffer = new char[literalLength];
					int read = 0;
					while (read < literalLength)
					{
						int n = await _reader.ReadAsync(buffer.AsMemory(read, literalLength - read), ct)
							.ConfigureAwait(false);
						if (n == 0)
							throw new BackendException("The ManageSieve server closed mid-literal.");
						read += n;
					}

					line = line[..open] + Quote(new string(buffer).Replace("\r\n", " "));
				}
			}

			lines.Add(line);
		}
	}

	private sealed record SieveResponse(string StatusLine, IReadOnlyList<string> Lines)
	{
		public bool IsOk => StatusLine.StartsWith("OK", StringComparison.OrdinalIgnoreCase);

		public void ThrowUnlessOk(string operation)
		{
			if (!IsOk)
				throw new BackendException($"ManageSieve {operation} failed: {StatusLine}");
		}
	}
}
