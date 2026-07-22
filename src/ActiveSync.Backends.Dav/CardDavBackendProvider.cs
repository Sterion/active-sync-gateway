using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
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

	public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role)
	{
		return
		[
			new BackendConfigField("BaseUrl", "Base URL", BackendFieldType.Url, Required: true,
				Help: "Absolute http(s) URL of the CardDAV server, e.g. https://dav.example.com."),
			new BackendConfigField("HomeSetPath", "Home set path", BackendFieldType.String,
				Help: "Path template of the user's address book home set — {user} and {localpart} are " +
				      "substituted, e.g. \"/{user}/\". Empty discovers it via .well-known."),
			.. BackendSchemaFields.Network()
		];
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		DavServerOptions options = settings.Bind<DavServerOptions>();
		return $"carddav {options.BaseUrl} " +
		       $"(cert={BackendDescription.DescribeCert(options.AllowInvalidCertificates, options.CaCertificatePath)})";
	}

	public Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct)
	{
		DavServerOptions options = settings.Bind<DavServerOptions>();
		return DavReadiness.ProbeAsync(
			options.BaseUrl, options.AllowInvalidCertificates, options.CaCertificatePath, ct);
	}

	public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.Contacts);
		DavServerOptions options = role.Settings.Bind<DavServerOptions>();
		WebDavClient client = new(new Uri(options.BaseUrl), role.Credentials,
			options.AllowInvalidCertificates, options.CaCertificatePath, _wireLogger);
		CardDavStore store = new(client, options, role.Credentials, _logger);
		return Task.FromResult<IBackendConnection>(new BackendConnection([store], ownedResources: [client]));
	}
}
