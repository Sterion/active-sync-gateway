using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   The "caldav" provider: fills the Calendar role with <see cref="CalDavStore" /> and the
///   Tasks role with <see cref="CalDavTaskStore" />, both over one shared
///   <see cref="WebDavClient" /> per account connection. A Tasks section served by the same
///   connection inherits the Calendar section's settings (BaseUrl etc.) as its base — it
///   typically only sets TaskFolder.
/// </summary>
public sealed class CalDavBackendProvider(ILoggerFactory loggerFactory) : IBackendProvider, IReadinessSource
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
		return DavReadiness.ProbeAsync(settings.Bind<DavServerOptions>().BaseUrl, ct);
	}

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole? calendarRole = context.Roles.FirstOrDefault(r => r.Role == BackendRole.Calendar);
		DavServerOptions clientOptions = (calendarRole ?? context.Roles[0]).Settings.Bind<DavServerOptions>();
		WebDavClient client = new(new Uri(clientOptions.BaseUrl), context.Roles[0].Credentials,
			clientOptions.AllowInvalidCertificates, clientOptions.CaCertificatePath, _wireLogger);
		string partStatIdentity = context.MailAddress ?? context.GatewayCredentials.UserName;

		List<IContentStore> stores = new();
		foreach (ResolvedRole role in context.Roles)
			switch (role.Role)
			{
				case BackendRole.Calendar:
					stores.Add(new CalDavStore(client, BindFor(role, calendarRole), role.Credentials,
						partStatIdentity, _logger, MergeSharedCollections(clientOptions, context)));
					break;
				case BackendRole.Tasks:
					stores.Add(new CalDavTaskStore(client, BindFor(role, calendarRole), role.Credentials, _logger));
					break;
				default:
					throw new InvalidOperationException($"caldav cannot serve the {role.Role} role.");
			}

		return new BackendConnection(stores, ownedResources: [client]);
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
