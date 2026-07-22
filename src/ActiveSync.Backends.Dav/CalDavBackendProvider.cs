using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   The "caldav" provider: fills the Calendar role with <see cref="CalDavStore" /> and the
///   Tasks role with <see cref="CalDavTaskStore" />, both over one shared
///   <see cref="WebDavClient" /> per account connection. A Tasks section served by the same
///   connection inherits the Calendar section's settings (BaseUrl etc.) as its base — it
///   typically only sets TaskFolder.
/// </summary>
public sealed class CalDavBackendProvider(
	IOptionsMonitor<ActiveSyncOptions> hostOptions, ILoggerFactory loggerFactory)
	: IBackendProvider, IReadinessSource
{
	private static readonly IReadOnlySet<BackendRole> Roles =
		new HashSet<BackendRole> { BackendRole.Calendar, BackendRole.Tasks };

	private readonly ILogger _logger = loggerFactory.CreateLogger<CalDavBackendProvider>();
	private readonly ILogger _wireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Dav");

	public string Name => "caldav";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		DavServerOptions options = settings.Bind<DavServerOptions>();
		string context = $"caldav ({role})";
		if (role == BackendRole.Tasks)
		{
			// The Tasks section is an overlay on the Calendar section when both roles run on
			// this provider — only keys it actually sets are checkable in isolation.
			if (settings.Section["BaseUrl"] is { } taskBaseUrl)
				BackendSettingsValidation.AbsoluteHttpUrl(taskBaseUrl, context, failures);
			if (settings.Section["TaskFolder"] == "")
				failures.Add($"{context}: TaskFolder must not be empty — name the VTODO collection.");
			return;
		}

		BackendSettingsValidation.AbsoluteHttpUrl(options.BaseUrl, context, failures);
		BackendSettingsValidation.Choice(options.CalendarAttachments, "CalendarAttachments", context, failures,
			"Auto", "On", "Off");
		BackendSettingsValidation.Choice(options.SendInvitations, "SendInvitations", context, failures,
			"Auto", "On", "Off");
		BackendSettingsValidation.CaPath(options.CaCertificatePath, context, failures);
		foreach (string entry in options.SharedCollections ?? [])
			if (SharedCollection.Validate(entry, options.BaseUrl) is { } sharedFailure)
				failures.Add($"{context}: SharedCollections: {sharedFailure}");
	}

	public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role)
	{
		// The Tasks section is an overlay on the Calendar one when both run here, so its URL
		// fields are optional — typically it only names the VTODO collection.
		if (role == BackendRole.Tasks)
			return
			[
				new BackendConfigField("TaskFolder", "VTODO collection", BackendFieldType.String, Default: "Tasks",
					Help: "Display name or path segment of the tasks collection in the calendar home set. " +
					      "Empty stores tasks in the gateway database instead.",
					SelfServiceEditable: true),
				new BackendConfigField("BaseUrl", "Base URL", BackendFieldType.Url,
					Help: "Only when tasks live on a different server than the calendar."),
				new BackendConfigField("HomeSetPath", "Home set path", BackendFieldType.String,
					Help: "Overrides the calendar section's home set for tasks.")
			];

		return
		[
			new BackendConfigField("BaseUrl", "Base URL", BackendFieldType.Url, Required: true,
				Help: "Absolute http(s) URL of the CalDAV server, e.g. https://dav.example.com."),
			new BackendConfigField("HomeSetPath", "Home set path", BackendFieldType.String,
				Help: "Path template of the user's collection home set — {user} and {localpart} are substituted, " +
				      "e.g. \"/{user}/\". Empty discovers it via .well-known and current-user-principal."),
			// The three below are the account holder's own preferences, and none of them can
			// move the connection: SharedCollections entries are validated against BaseUrl's
			// host, which stays admin-only.
			new BackendConfigField("CalendarAttachments", "Event attachments", BackendFieldType.Enum,
				Default: "Auto", EnumValues: ["Auto", "On", "Off"],
				Help: "Inline (base64) attachments for EAS 16.x clients. Auto caps them at 1 MiB, On at 16 MiB.",
				SelfServiceEditable: true),
			new BackendConfigField("SendInvitations", "Send iMIP invitations", BackendFieldType.Enum,
				Default: "Auto", EnumValues: ["Auto", "On", "Off"],
				Help: "Auto sends unless the server advertises a scheduling outbox and invites on its own.",
				SelfServiceEditable: true),
			new BackendConfigField("SharedCollections", "Extra calendar collections", BackendFieldType.StringList,
				Help: "Absolute paths or same-host URLs synced as additional calendar folders, " +
				      "each optionally suffixed \"|ro\" for read-only.",
				SelfServiceEditable: true),
			.. BackendSchemaFields.Network()
		];
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		DavServerOptions options = settings.Bind<DavServerOptions>();
		return role == BackendRole.Tasks
			? $"caldav VTODO collection \"{(string.IsNullOrWhiteSpace(options.TaskFolder) ? "Tasks" : options.TaskFolder)}\""
			: $"caldav {options.BaseUrl} (attachments={options.CalendarAttachments}, " +
			  $"invitations={options.SendInvitations}, shared={options.SharedCollections?.Count ?? 0})";
	}

	public Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct)
	{
		DavServerOptions options = settings.Bind<DavServerOptions>();
		return DavReadiness.ProbeAsync(
			options.BaseUrl, options.AllowInvalidCertificates, options.CaCertificatePath, ct);
	}

	public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
	{
		ResolvedRole? calendarRole = context.Roles.FirstOrDefault(r => r.Role == BackendRole.Calendar);
		// H21: the shared client's settings (BaseUrl, TLS) and its credentials must come from the
		// SAME role. The old code took settings from the Calendar role but credentials from
		// Roles[0] — so when Tasks was listed first with its own per-user credentials, the client
		// hit the calendar server authenticating as the tasks role.
		ResolvedRole clientRole = SelectClientRole(context.Roles);
		DavServerOptions clientOptions = clientRole.Settings.Bind<DavServerOptions>();
		WebDavClient client = new(new Uri(clientOptions.BaseUrl), clientRole.Credentials,
			clientOptions.AllowInvalidCertificates, clientOptions.CaCertificatePath, _wireLogger);
		string partStatIdentity = context.MailAddress ?? context.GatewayCredentials.UserName;
		int pollSeconds = hostOptions.CurrentValue.Eas.DavPollSeconds;

		List<IContentStore> stores = new();
		foreach (ResolvedRole role in context.Roles)
			switch (role.Role)
			{
				case BackendRole.Calendar:
					stores.Add(new CalDavStore(client, BindFor(role, calendarRole), role.Credentials,
						partStatIdentity, _logger, pollSeconds, MergeSharedCollections(clientOptions, context)));
					break;
				case BackendRole.Tasks:
					stores.Add(new CalDavTaskStore(client, BindFor(role, calendarRole), role.Credentials,
						_logger, pollSeconds));
					break;
				default:
					throw new InvalidOperationException($"caldav cannot serve the {role.Role} role.");
			}

		return Task.FromResult<IBackendConnection>(new BackendConnection(stores, ownedResources: [client]));
	}

	/// <summary>
	///   The one role whose settings AND credentials build the shared <see cref="WebDavClient" />:
	///   the Calendar role when present (it carries the connection's BaseUrl/TLS), else the first
	///   assigned role. Both must come from this single role so the client never authenticates as a
	///   different role than the one whose endpoint it is talking to (H21).
	/// </summary>
	internal static ResolvedRole SelectClientRole(IReadOnlyList<ResolvedRole> roles)
	{
		return roles.FirstOrDefault(r => r.Role == BackendRole.Calendar) ?? roles[0];
	}

	/// <summary>The Tasks role inherits the Calendar section as its base when both are assigned here.</summary>
	private static DavServerOptions BindFor(ResolvedRole role, ResolvedRole? calendarRole)
	{
		if (role.Role != BackendRole.Tasks || calendarRole is null || ReferenceEquals(role, calendarRole))
			return role.Settings.Bind<DavServerOptions>();
		DavServerOptions merged = calendarRole.Settings.Bind<DavServerOptions>();
		role.Settings.Section.Bind(merged);
		return merged;
	}

	/// <summary>
	///   Config SharedCollections ∪ the host's runtime `eas share` grants; a grant for the
	///   same collection overrides the config entry's mode.
	/// </summary>
	private static IReadOnlyList<SharedCollection> MergeSharedCollections(
		DavServerOptions options, BackendConnectionContext context)
	{
		List<SharedCollection> merged = (options.SharedCollections ?? [])
			.Select(SharedCollection.Parse)
			.ToList();
		foreach (SharedCollection grant in context.SharedCollections)
		{
			merged.RemoveAll(c => c.Href.TrimEnd('/') == grant.Href.TrimEnd('/'));
			merged.Add(grant);
		}

		return merged;
	}
}
