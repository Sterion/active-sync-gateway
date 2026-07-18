using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;

namespace ActiveSync.Backends.Local;

/// <summary>
///   The "local" provider: serves content classes from the gateway database when no external
///   backend is configured (and always Notes — no DAV backend carries them). Stores are keyed
///   and encrypted by the GATEWAY identity, never a backend login.
/// </summary>
public sealed class LocalBackendProvider(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	LocalContentProtector protector) : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole>
	{
		BackendRole.Calendar, BackendRole.Tasks, BackendRole.Contacts, BackendRole.Notes
	};

	public string Name => "local";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		BackendCredentials gateway = context.GatewayCredentials;
		string partStatIdentity = context.MailAddress ?? gateway.UserName;
		List<IContentStore> stores = new();
		foreach (ResolvedRole role in context.Roles)
			stores.Add(role.Role switch
			{
				BackendRole.Calendar => new LocalCalendarStore(dbFactory, notifier, gateway, protector, partStatIdentity),
				BackendRole.Tasks => new LocalTaskStore(dbFactory, notifier, gateway, protector),
				BackendRole.Contacts => new LocalContactStore(dbFactory, notifier, gateway, protector),
				BackendRole.Notes => new LocalNotesStore(dbFactory, notifier, gateway, protector),
				_ => throw new InvalidOperationException($"local cannot serve the {role.Role} role.")
			});
		return new BackendConnection(stores);
	}
}
