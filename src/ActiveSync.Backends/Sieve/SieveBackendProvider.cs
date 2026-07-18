using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Sieve;

/// <summary>The "sieve" provider: fills the Oof role with <see cref="SieveOofBackend" />.</summary>
public sealed class SieveBackendProvider(ILoggerFactory loggerFactory) : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.Oof };

	private readonly ILogger _wireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Sieve");

	public string Name => "sieve";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.Oof);
		SieveOofBackend oof = new((SieveOptions)role.Settings!, role.Credentials, _wireLogger);
		return new BackendConnection([], oof: oof);
	}
}
