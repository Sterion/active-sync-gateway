using ActiveSync.Backends.Imap;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>The "carddav" provider: fills the Contacts role with <see cref="CardDavStore" />.</summary>
public sealed class CardDavBackendProvider(ILoggerFactory loggerFactory) : IBackendProvider, IReadinessSource
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.Contacts };

	private readonly ILogger _logger = loggerFactory.CreateLogger<CardDavBackendProvider>();
	private readonly ILogger _wireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Dav");

	public string Name => "carddav";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		DavServerOptions options = settings.Bind<DavServerOptions>();
		string context = $"carddav ({role})";
		BackendSettingsValidation.AbsoluteHttpUrl(options.BaseUrl, context, failures);
		BackendSettingsValidation.CaPath(options.CaCertificatePath, context, failures);
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		DavServerOptions options = settings.Bind<DavServerOptions>();
		return $"carddav {options.BaseUrl} " +
		       $"(cert={ImapBackendProvider.DescribeCert(options.AllowInvalidCertificates, options.CaCertificatePath)})";
	}

	public Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct)
	{
		return DavReadiness.ProbeAsync(settings.Bind<DavServerOptions>().BaseUrl, ct);
	}

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.Contacts);
		DavServerOptions options = role.Settings.Bind<DavServerOptions>();
		WebDavClient client = new(new Uri(options.BaseUrl), role.Credentials,
			options.AllowInvalidCertificates, options.CaCertificatePath, _wireLogger);
		CardDavStore store = new(client, options, role.Credentials, _logger);
		return new BackendConnection([store], ownedResources: [client]);
	}
}
