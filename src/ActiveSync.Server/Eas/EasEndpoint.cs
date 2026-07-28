using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Http;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.DependencyInjection;
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

	/// <summary>User label for a request that has not authenticated yet — see <see cref="MetricsKey" />.</summary>
	public const string AnonymousMetricsUser = "-";

	// 2.5/12.0 were dropped from the advertisement when 16.x arrived: this gateway never
	// implemented their exclusive commands (GetHierarchy, *Collection), so advertising
	// them was always a lie a real 2.5 client would have tripped over.
	private const string ProtocolVersions = "12.1,14.0,14.1,16.0,16.1";

	private const string ProtocolCommands =
		"Sync,SendMail,SmartForward,SmartReply,GetAttachment,FolderSync,FolderCreate,FolderDelete," +
		"FolderUpdate,MoveItems,GetItemEstimate,MeetingResponse,Search,Settings,Ping,ItemOperations," +
		"Provision,ResolveRecipients,Find";

	/// <summary>
	///   Case-insensitive map from any casing of a client-supplied Cmd to the canonical command
	///   name, which is also the DI key each handler is registered under (see
	///   <see cref="Setup.ServiceCollectionExtensions.AddEasHandlers" />). MS-ASHTTP treats the
	///   command case-insensitively; keyed DI does exact-match, so the request command is mapped
	///   to its canonical form before resolving the handler.
	/// </summary>
	private static readonly Dictionary<string, string> CanonicalCommands =
		ProtocolCommands.Split(',').ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);

	/// <summary>The canonical EAS command names this endpoint advertises and dispatches.</summary>
	public static IReadOnlyList<string> AdvertisedCommands { get; } = ProtocolCommands.Split(',');

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
		AuthThrottle authThrottle,
		IOptionsMonitor<ActiveSyncOptions> options,
		BackendRolesProvider rolesProvider,
		UserProvisioner provisioner,
		UserResolver userResolver,
		ILoggerFactory loggerFactory)
	{
		ILogger logger = loggerFactory.CreateLogger("ActiveSync.Endpoint");
		CancellationToken ct = http.RequestAborted;

		// Unconfigured gateway (no mail backend yet): answer 503 until it is configured via
		// `eas config set` (applied within ~1s by the settings change-stamp poll).
		if (!rolesProvider.Current.IsMailConfigured)
		{
			logger.LogWarning("EAS request refused: the gateway has no mail backend configured (503)");
			http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			return;
		}

		// --- Basic auth ---
		string clientKey = EndpointAuth.ClientKey(http, options.CurrentValue.Auth);
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

		// Metric label values are time series, so the username may NOT come from an
		// unauthenticated caller: until the credentials verify, the user label is the same "-"
		// GatewayMetrics uses when per-user labels are off. 401/429 outcomes are still counted
		// (the command label is clamped to the known set inside GatewayMetrics), just anonymously.
		http.Items[MetricsKey] = (parameters.Command, AnonymousMetricsUser);

		if (!await EndpointAuth.AuthenticateAsync(
			    http, sessionFactory, authThrottle, clientKey, credentials, logger, ct))
			return;

		// Authenticated: the username is now a real account, so it is safe as a label.
		http.Items[MetricsKey] = (parameters.Command, LogText.Clean(credentials.UserName, 128));

		// Identity is total past the auth boundary: every authenticated login has a user row and
		// a known immutable UserId before anything user-scoped runs, so no handler, store,
		// notifier or cache further down needs a "not provisioned yet" branch. On first sign-in
		// this mints the row (an auto-provisioned declaration for undeclared logins; identity-only
		// for config-declared ones). Runs before the block check so an operator can see (and
		// block) even a user they intend to block.
		int? ensuredUserId = await provisioner.EnsureUserAsync(credentials.UserName, ct);
		if (ensuredUserId is not { } userId)
		{
			http.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		// A disabled account (eas user disable) refuses every device; operator blocks (eas
		// block/unblock) are the ad-hoc/device-scoped variant. Both are enforced after auth so
		// only holders of valid credentials can observe them, through the same shared decision the
		// Autodiscover prologue uses (EndpointAuth.CheckLoginRefusalAsync) rather than a second copy
		// that could drift out of sync with it.
		// 403, not 401 — a challenge would loop the client through credential prompts.
		LoginRefusal refusal = await EndpointAuth.CheckLoginRefusalAsync(
			userResolver, state, credentials.UserName, userId, parameters.DeviceId, ct);
		if (refusal != LoginRefusal.None)
		{
			logger.LogWarning("Refused {State} EAS login {User} ({DeviceId})",
				refusal == LoginRefusal.Disabled ? "disabled" : "blocked",
				LogText.Clean(credentials.UserName, 128), parameters.DeviceId);
			http.Response.StatusCode = StatusCodes.Status403Forbidden;
			await http.Response.WriteAsync(refusal == LoginRefusal.Disabled
				? "This account is disabled on the gateway."
				: "This account or device is blocked on the gateway.", ct);
			return;
		}

		// Resolve exactly the one handler this command needs (keyed scoped) instead of
		// constructing every handler and picking one. The incoming command is canonicalized to
		// its registered key first (case-insensitive, per MS-ASHTTP) so "ping" finds "Ping".
		IEasCommandHandler? handler =
			CanonicalCommands.TryGetValue(parameters.Command, out string? canonical)
				? http.RequestServices.GetKeyedService<IEasCommandHandler>(canonical)
				: null;
		if (handler is null)
		{
			logger.LogWarning("Unsupported EAS command {Command}", LogText.Clean(parameters.Command, 32));
			http.Response.StatusCode = StatusCodes.Status501NotImplemented;
			return;
		}

		Device device = await state.GetOrCreateDeviceAsync(
			userId, parameters.DeviceId, parameters.DeviceType, ct,
			parameters.ProtocolVersion);

		// Pending account-only wipe (16.1): herd the device into Provision, where the wipe
		// directive is delivered — every other command gets 449 like an unprovisioned device.
		if (device.PendingAccountWipe &&
		    !parameters.Command.Equals("Provision", StringComparison.OrdinalIgnoreCase))
		{
			// AccountOnlyRemoteWipe (MS-ASPROV token 0x3B) is a 16.1-only element. A pre-16.1
			// device herded into Provision cannot decode the directive, never sends the Status-1
			// acknowledgment, and so never completes the wipe — every command 449s forever. Complete
			// the wipe server-side instead (the same terminal state a 16.1 device reaches once it
			// acknowledges) rather than waiting on an acknowledgment that can never arrive.
			if (EasVersion.Parse(parameters.ProtocolVersion) < EasVersion.V161)
			{
				logger.LogInformation(
					"Account wipe pending for {User} ({DeviceId}) on pre-16.1 client (EAS {Version}); " +
					"completing it server-side instead of herding into an undecodable Provision",
					LogText.Clean(credentials.UserName, 128), parameters.DeviceId,
					parameters.ProtocolVersion ?? "(unknown)");
				await state.CompleteAccountWipeAsync(device, ct);
				http.Response.StatusCode = StatusCodes.Status403Forbidden;
				await http.Response.WriteAsync("This account or device is blocked on the gateway.", ct);
				return;
			}

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
		PolicyOptions policy = options.CurrentValue.Policy;
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

		// The returned session is a lease — disposing it at the end of the request releases the
		// lease (the cache keeps the connection alive for reuse; the last release tears it down).
		await using IBackendSession session =
			await sessionFactory.GetSessionAsync(credentials, userId, parameters.DeviceId, ct);

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
		catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
		{
			// Not the client going away (that is the first catch): a backend library timeout.
			// MailKit implements its per-op Timeout by cancelling an INTERNAL token, so a hung
			// IMAP/SMTP op surfaces as an OCE whose token is not http.RequestAborted. Log it and
			// answer 503 instead of letting it bubble to the framework unlogged.
			logger.LogWarning(ex, "EAS {Command} timed out talking to the backend for {User}/{DeviceId}",
				LogText.Clean(parameters.Command, 32), LogText.Clean(credentials.UserName, 128), parameters.DeviceId);
			if (!http.Response.HasStarted)
				http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
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
	///   raw bytes); a few punctuation characters are tolerated for older clients.
	///   <c>internal</c> so <c>EasEndpointDeviceIdTests</c> can exercise it directly.
	/// </summary>
	internal static bool IsValidDeviceId(string deviceId)
	{
		// An empty DeviceId used to be let through on the theory that some tools (e.g.
		// OPTIONS probes) omit it — but OPTIONS is mapped separately (HandleOptions) and never
		// reaches this check, so the only effect was every POST that omitted DeviceId sharing one
		// "" keyed Device row (SyncKeys, snapshots, PolicyKey, ...) for that user. MS-ASHTTP
		// requires DeviceId on a POST; reject the empty case.
		if (deviceId.Length == 0)
			return false;
		if (deviceId.Length > 64)
			return false;
		foreach (char c in deviceId)
			if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
				return false;
		return true;
	}
}
