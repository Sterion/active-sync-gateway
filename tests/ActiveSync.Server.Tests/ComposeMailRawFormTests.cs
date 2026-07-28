using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Http;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   F26 (round 3) — the 12.x raw <c>message/rfc822</c> SendMail wire form defines success as an
///   empty 200 and errors as HTTP status codes (MS-ASHTTP); there is no WBXML body in this form at
///   all. ComposeMailHandlerBase.WriteErrorAsync always wrote a ComposeMail WBXML response, which
///   belongs to the 14.x/16.x form — a 12.x client expecting a bare status may never see the
///   failure.
/// </summary>
public sealed class ComposeMailRawFormTests : IDisposable
{
	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	[Fact]
	public async Task RawFormSendMail_WithEmptyBody_FailsWithHttpStatusNotWbxmlBody()
	{
		EasContext context = await NewRawContextAsync([]);

		SendMailHandler handler = new(
			_harness.Folders, TestOptionsMonitor.SnapshotOf(_harness.Options),
			NullLogger<SendMailHandler>.Instance);

		await handler.HandleAsync(context, CancellationToken.None);

		Assert.NotEqual(StatusCodes.Status200OK, context.Http.Response.StatusCode);
		Assert.Equal(0, ((MemoryStream)context.Http.Response.Body).Length);
		Assert.Empty(_harness.Session.Submit.Sent);
	}

	private async Task<EasContext> NewRawContextAsync(byte[] rawMime)
	{
		DefaultHttpContext http = new();
		http.Request.ContentType = "message/rfc822";
		http.Request.Body = new MemoryStream(rawMime);
		http.Request.ContentLength = rawMime.Length;
		http.Response.Body = new MemoryStream();
		Device device = await _harness.State.GetOrCreateDeviceAsync(
			_harness.UserId, "TESTDEVICE01", "TestClient", CancellationToken.None);
		return new EasContext
		{
			Http = http,
			Parameters = new EasRequestParameters { Command = "SendMail", DeviceId = device.DeviceId },
			Credentials = new BackendCredentials(EasHandlerHarness.UserName, "pw"),
			Session = _harness.Session,
			Device = device,
			State = _harness.State,
			WireLogger = NullLogger.Instance
		};
	}
}
