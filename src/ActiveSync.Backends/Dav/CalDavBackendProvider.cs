using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   The "caldav" provider: fills the Calendar role with <see cref="CalDavStore" /> and the
///   Tasks role with <see cref="CalDavTaskStore" />, both over one shared
///   <see cref="WebDavClient" /> per account connection.
/// </summary>
public sealed class CalDavBackendProvider(ILoggerFactory loggerFactory) : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles =
		new HashSet<BackendRole> { BackendRole.Calendar, BackendRole.Tasks };

	private readonly ILogger _logger = loggerFactory.CreateLogger<CalDavBackendProvider>();
	private readonly ILogger _wireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Dav");

	public string Name => "caldav";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		DavServerOptions clientOptions = (DavServerOptions)context.Roles[0].Settings!;
		WebDavClient client = new(new Uri(clientOptions.BaseUrl), context.Roles[0].Credentials,
			clientOptions.AllowInvalidCertificates, clientOptions.CaCertificatePath, _wireLogger);
		string partStatIdentity = context.MailAddress ?? context.GatewayCredentials.UserName;

		List<IContentStore> stores = new();
		foreach (ResolvedRole role in context.Roles)
			stores.Add(role.Role switch
			{
				BackendRole.Calendar => new CalDavStore(client, (DavServerOptions)role.Settings!,
					role.Credentials, partStatIdentity, _logger, context.SharedCollections),
				BackendRole.Tasks => new CalDavTaskStore(client, (DavServerOptions)role.Settings!,
					role.Credentials, _logger),
				_ => throw new InvalidOperationException($"caldav cannot serve the {role.Role} role.")
			});
		return new BackendConnection(stores, ownedResources: [client]);
	}
}
