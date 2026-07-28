using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Local;

/// <summary>
///   The "local" provider: serves content classes from the gateway database when no external
///   backend is configured (and always Notes — no DAV backend carries them). Stores are keyed
///   and encrypted by the GATEWAY identity, never a backend login.
/// </summary>
public sealed class LocalBackendProvider(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	LocalContentProtector protector,
	ILoggerFactory loggerFactory) : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole>
	{
		BackendRole.Calendar, BackendRole.Tasks, BackendRole.Contacts, BackendRole.Notes
	};

	public string Name => "local";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		// Deliberately lenient: the local provider ignores settings rather than rejecting
		// them, so a role falling back to local (Enabled=false, upgraded legacy rows) can
		// never invalidate an account because stale provider settings ride along.
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		return "gateway database (encrypted at rest)";
	}

	public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
	{
		int userId = context.GatewayUserId;
		string gatewayLogin = context.GatewayCredentials.UserName;
		string partStatIdentity = context.MailAddress ?? gatewayLogin;
		List<IContentStore> stores = new();
		foreach (ResolvedRole role in context.Roles)
			stores.Add(role.Role switch
			{
				BackendRole.Calendar => new LocalCalendarStore(
					dbFactory, notifier, userId, protector, gatewayLogin, partStatIdentity,
					loggerFactory.CreateLogger<LocalCalendarStore>()),
				BackendRole.Tasks => new LocalTaskStore(dbFactory, notifier, userId, protector),
				BackendRole.Contacts => new LocalContactStore(
					dbFactory, notifier, userId, protector, loggerFactory.CreateLogger<LocalContactStore>()),
				BackendRole.Notes => new LocalNotesStore(dbFactory, notifier, userId, protector),
				_ => throw new InvalidOperationException($"local cannot serve the {role.Role} role.")
			});
		return Task.FromResult<IBackendConnection>(new BackendConnection(stores));
	}
}
