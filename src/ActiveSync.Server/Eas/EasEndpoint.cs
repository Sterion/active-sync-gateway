using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Http;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ActiveSync.Server.Eas;

/// <summary>The /Microsoft-Server-ActiveSync endpoint (MS-ASHTTP).</summary>
public static class EasEndpoint
{
	public const string Path = "/Microsoft-Server-ActiveSync";

	/// <summary>HttpContext.Items key carrying "EAS {Cmd} {User} ({DeviceId})" for request logging.</summary>
	public const string RequestSummaryKey = "EasRequestSummary";

	/// <summary>HttpContext.Items key carrying (command, user) for the metrics middleware.</summary>
	public const string MetricsKey = "EasMetrics";

	// 2.5/12.0 were dropped from the advertisement when 16.x arrived: this gateway never
	// implemented their exclusive commands (GetHierarchy, *Collection), so advertising
	// them was always a lie a real 2.5 client would have tripped over.
	private const string ProtocolVersions = "12.1,14.0,14.1,16.0,16.1";

	private const string ProtocolCommands =
		"Sync,SendMail,SmartForward,SmartReply,GetAttachment,FolderSync,FolderCreate,FolderDelete," +
		"FolderUpdate,MoveItems,GetItemEstimate,MeetingResponse,Search,Settings,Ping,ItemOperations," +
		"Provision,ResolveRecipients,Find";

	public static void Map(WebApplication app)
	{
		app.MapMethods(Path, ["OPTIONS"], HandleOptions);
		app.MapMethods(Path, ["POST"], HandlePost);
	}

	private static IResult HandleOptions(HttpContext http)
	{
		http.Response.Headers["MS-ASProtocolVersions"] = ProtocolVersions;
		http.Response.Headers["MS-ASProtocolCommands"] = ProtocolCommands;
		http.Response.Headers["MS-Server-ActiveSync"] = "14.1";
		return Results.Ok();
	}

	private static async Task HandlePost(
		HttpContext http,
		IBackendSessionFactory sessionFactory,
		SyncStateService state,
		IEnumerable<IEasCommandHandler> handlers,
		AuthThrottle authThrottle,
		IOptionsSnapshot<ActiveSyncOptions> options,
		ILoggerFactory loggerFactory)
	{
		ILogger logger = loggerFactory.CreateLogger("ActiveSync.Endpoint");
		CancellationToken ct = http.RequestAborted;

		// --- Basic auth ---
		string clientKey = EndpointAuth.ClientKey(http);
		if (EndpointAuth.IsThrottled(http, authThrottle, clientKey))
			return;
		BackendCredentials? credentials = HttpBasicAuth.Parse(http.Request.Headers.Authorization.ToString());
		if (credentials is null)
		{
			HttpBasicAuth.Challenge(http);
			return;
		}

		// --- Query parameters (plain or base64) ---
		EasRequestParameters parameters;
		try
		{
			string query = http.Request.QueryString.Value?.TrimStart('?') ?? "";
			// Plain form is detected by the Cmd key — a padded base64 query also contains
			// '=' characters, so testing for '=' alone would misroute real 12.1+ clients.
			if (http.Request.Query.ContainsKey("Cmd"))
			{
				parameters = EasRequestParameters.FromQuery(
					http.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()));
			}
			else if (query.Length > 0)
			{
				parameters = EasRequestParameters.FromBase64(Uri.UnescapeDataString(query));
			}
			else
			{
				http.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}
		}
		catch (FormatException ex)
		{
			logger.LogWarning(ex, "Malformed EAS query string");
			http.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		// Plain-query clients carry the protocol version in a header.
		if (http.Request.Headers.TryGetValue("MS-ASProtocolVersion", out StringValues versionHeader) &&
		    !string.IsNullOrEmpty(versionHeader.ToString()))
			parameters = parameters with { ProtocolVersion = versionHeader.ToString() };

		// Device ids key the sync-state tables and appear in log lines; anything outside
		// the MS-ASHTTP shape (alphanumeric-ish, short) is a hand-crafted request.
		if (!IsValidDeviceId(parameters.DeviceId))
		{
			logger.LogWarning("Rejected EAS request with malformed device id {DeviceId}",
				LogText.Clean(parameters.DeviceId, 64));
			http.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		// Picked up by the Serilog request-completion line so it reads
		// "EAS Sync user@host (deviceid) responded 200 ..." instead of the raw POST path.
		// Username and command are client-controlled text — sanitized before logging.
		http.Items[RequestSummaryKey] =
			$"EAS {LogText.Clean(parameters.Command, 32)} {LogText.Clean(credentials.UserName, 128)} ({parameters.DeviceId})";
		http.Items[MetricsKey] =
			(LogText.Clean(parameters.Command, 32), LogText.Clean(credentials.UserName, 128));

		if (!await EndpointAuth.AuthenticateAsync(
			    http, sessionFactory, authThrottle, clientKey, credentials, logger, ct))
			return;

		// Operator blocks (eas block/unblock) are enforced after auth so only holders of valid
		// credentials can observe them. 403, not 401 — a challenge would loop the client
		// through credential prompts.
		if (await state.IsLoginBlockedAsync(credentials.UserName, parameters.DeviceId, ct))
		{
			logger.LogWarning("Refused blocked EAS login {User} ({DeviceId})",
				LogText.Clean(credentials.UserName, 128), parameters.DeviceId);
			http.Response.StatusCode = StatusCodes.Status403Forbidden;
			await http.Response.WriteAsync("This account or device is blocked on the gateway.", ct);
			return;
		}

		IEasCommandHandler? handler =
			handlers.FirstOrDefault(h => h.Command.Equals(parameters.Command, StringComparison.OrdinalIgnoreCase));
		if (handler is null)
		{
			logger.LogWarning("Unsupported EAS command {Command}", LogText.Clean(parameters.Command, 32));
			http.Response.StatusCode = StatusCodes.Status501NotImplemented;
			return;
		}

		Device device = await state.GetOrCreateDeviceAsync(
			credentials.UserName, parameters.DeviceId, parameters.DeviceType, ct,
			parameters.ProtocolVersion);

		// Pending account-only wipe (16.1): herd the device into Provision, where the wipe
		// directive is delivered — every other command gets 449 like an unprovisioned device.
		if (device.PendingAccountWipe &&
		    !parameters.Command.Equals("Provision", StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation("Account wipe pending for {User} ({DeviceId}); forcing Provision",
				LogText.Clean(credentials.UserName, 128), parameters.DeviceId);
			http.Response.StatusCode = 449;
			return;
		}

		// Policy enforcement (MS-ASPROV): once a policy is configured, every command except
		// Provision itself requires the device to present its current policy key AND to have
		// acknowledged the CURRENT policy document (config changes change the hash). HTTP 449
		// tells the client to run the Provision handshake and retry. Checked before a backend
		// session is built — a 449 answer needs no IMAP/DAV connections.
		PolicyOptions policy = options.Value.Policy;
		if (policy.Enabled && !parameters.Command.Equals("Provision", StringComparison.OrdinalIgnoreCase))
		{
			uint presentedKey = parameters.PolicyKey;
			if (presentedKey == 0 &&
			    http.Request.Headers.TryGetValue("X-MS-PolicyKey", out StringValues policyKeyHeader) &&
			    uint.TryParse(policyKeyHeader.ToString(), out uint headerKey))
				presentedKey = headerKey;

			if (presentedKey == 0 || presentedKey != device.PolicyKey ||
			    !string.Equals(device.PolicyDocHash, PolicyDocument.Hash(policy), StringComparison.Ordinal))
			{
				logger.LogInformation("Policy re-provision required for {User} ({DeviceId}) on {Command}",
					LogText.Clean(credentials.UserName, 128), parameters.DeviceId,
					LogText.Clean(parameters.Command, 32));
				http.Response.StatusCode = 449; // MS-ASHTTP: Retry After Sending Provision Command
				return;
			}
		}

		IBackendSession session = await sessionFactory.GetSessionAsync(credentials, parameters.DeviceId, ct);

		EasContext context = new()
		{
			Http = http,
			Parameters = parameters,
			Credentials = credentials,
			Session = session,
			Device = device,
			State = state,
			WireLogger = loggerFactory.CreateLogger<EasContext>()
		};

		try
		{
			await handler.HandleAsync(context, ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// client went away — nothing to do
		}
		catch (WbxmlException ex)
		{
			logger.LogWarning(ex, "Bad WBXML from device {DeviceId} for {Command}",
				parameters.DeviceId, LogText.Clean(parameters.Command, 32));
			if (!http.Response.HasStarted)
				http.Response.StatusCode = StatusCodes.Status400BadRequest;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "EAS {Command} failed for {User}/{DeviceId}",
				LogText.Clean(parameters.Command, 32), LogText.Clean(credentials.UserName, 128), parameters.DeviceId);
			if (!http.Response.HasStarted)
				http.Response.StatusCode = StatusCodes.Status500InternalServerError;
		}
	}

	/// <summary>
	///   MS-ASHTTP device ids are short and alphanumeric (the base64 query form hex-encodes
	///   raw bytes); a few punctuation characters are tolerated for older clients. Empty is
	///   allowed — some commands (e.g. OPTIONS-probing tools) omit it.
	/// </summary>
	private static bool IsValidDeviceId(string deviceId)
	{
		if (deviceId.Length == 0)
			return true;
		if (deviceId.Length > 64)
			return false;
		foreach (char c in deviceId)
			if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
				return false;
		return true;
	}
}
