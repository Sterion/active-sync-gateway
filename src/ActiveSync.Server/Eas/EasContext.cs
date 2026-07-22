using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Http;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas;

/// <summary>Per-request state handed to EAS command handlers.</summary>
public sealed class EasContext
{
	private XDocument? _requestDocument;
	private bool _requestRead;
	public required HttpContext Http { get; init; }
	public required EasRequestParameters Parameters { get; init; }
	public required BackendCredentials Credentials { get; init; }
	public required IBackendSession Session { get; init; }
	public required Device Device { get; init; }
	public required SyncStateService State { get; init; }

	/// <summary>
	///   Verbose wire logging (category ActiveSync.Server.Eas.EasContext): every decoded
	///   request document and every response leaves a Trace event. Free when Trace is off.
	/// </summary>
	public required ILogger WireLogger { get; init; }

	public CancellationToken Aborted => Http.RequestAborted;

	/// <summary>The negotiated protocol version; gate 16.x behavior with >= EasVersion.V160.</summary>
	public EasVersion Version => EasVersion.Parse(Parameters.ProtocolVersion);

	private void TraceWire(string direction, string payload)
	{
		WireLogger.LogTrace("{Cmd} {User} ({Device}) {Direction}: {Payload}",
			LogText.Clean(Parameters.Command), LogText.Clean(Device.UserName),
			LogText.Clean(Device.DeviceId), direction, WireLog.Payload(payload));
	}

	/// <summary>Decodes the WBXML request body (null for empty bodies).</summary>
	public async Task<XDocument?> ReadRequestAsync()
	{
		if (_requestRead)
			return _requestDocument;
		_requestRead = true;
		// Treat a request as body-less only when it has neither a positive Content-Length nor
		// a Transfer-Encoding header: a chunked request (Transfer-Encoding: chunked) has no
		// Content-Length but still carries a body we must read. This shortcut is an HTTP/1.x
		// framing fact ONLY — HTTP/2 forbids Transfer-Encoding (RFC 9113 §8.2.2) and streamed
		// h2 bodies carry no Content-Length, so applying it there drops every request body
		// (E1). Under h2 (and h3) fall through and let the zero-length read below decide.
		bool http1 = HttpProtocol.IsHttp11(Http.Request.Protocol) || HttpProtocol.IsHttp10(Http.Request.Protocol);
		if (http1 && Http.Request.ContentLength is 0 or null && !Http.Request.Headers.ContainsKey("Transfer-Encoding"))
			return null;
		using MemoryStream buffer = new();
		await Http.Request.Body.CopyToAsync(buffer, Aborted);
		if (buffer.Length == 0)
			return null;
		_requestDocument = WbxmlDecoder.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
		if (WireLogger.IsEnabled(LogLevel.Trace) && _requestDocument is not null)
			TraceWire("request", _requestDocument.ToString());
		return _requestDocument;
	}

	/// <summary>Reads the raw request body (for message/rfc822 SendMail).</summary>
	public async Task<byte[]> ReadRawBodyAsync()
	{
		using MemoryStream buffer = new();
		await Http.Request.Body.CopyToAsync(buffer, Aborted);
		if (WireLogger.IsEnabled(LogLevel.Trace))
			// Size only: the MIME shows up verbatim on the SMTP wire log when that is enabled.
			TraceWire("request", $"(raw body, {buffer.Length} bytes)");
		return buffer.ToArray();
	}

	public async Task WriteResponseAsync(XDocument document)
	{
		if (WireLogger.IsEnabled(LogLevel.Trace))
			TraceWire("response", document.ToString());
		// Encode is pure CPU/in-memory work (no I/O) — EncodeAsync just calls it internally
		// before writing to a stream, and doesn't fit here since ContentLength must be set
		// from the encoded length before the body write starts.
#pragma warning disable VSTHRD103
		byte[] bytes = WbxmlEncoder.Encode(document);
#pragma warning restore VSTHRD103
		Http.Response.StatusCode = StatusCodes.Status200OK;
		Http.Response.ContentType = "application/vnd.ms-sync.wbxml";
		Http.Response.ContentLength = bytes.Length;
		await Http.Response.Body.WriteAsync(bytes, Aborted);
	}

	/// <summary>HTTP 200 with empty body — EAS "no changes" / "success without payload".</summary>
	public Task WriteEmptyAsync()
	{
		if (WireLogger.IsEnabled(LogLevel.Trace))
			TraceWire("response", "(empty 200)");
		Http.Response.StatusCode = StatusCodes.Status200OK;
		Http.Response.ContentLength = 0;
		return Task.CompletedTask;
	}

	public async Task WriteBinaryAsync(byte[] content, string contentType)
	{
		if (WireLogger.IsEnabled(LogLevel.Trace))
			TraceWire("response", $"(binary, {content.Length} bytes, {contentType})");
		Http.Response.StatusCode = StatusCodes.Status200OK;
		Http.Response.ContentType = contentType;
		Http.Response.ContentLength = content.Length;
		await Http.Response.Body.WriteAsync(content, Aborted);
	}
}
