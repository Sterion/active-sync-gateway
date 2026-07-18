using ActiveSync.Backends.Imap;
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

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		SieveOptions options = settings.Bind<SieveOptions>();
		string context = $"sieve ({role})";
		BackendSettingsValidation.RequiredHost(options.Host, context, failures);
		BackendSettingsValidation.Port(options.Port, context, failures);
		BackendSettingsValidation.CaPath(options.CaCertificatePath, context, failures);
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		SieveOptions options = settings.Bind<SieveOptions>();
		return $"sieve {options.Host}:{options.Port} (tls={(options.UseTls ? "on" : "off")}, " +
		       $"cert={ImapBackendProvider.DescribeCert(options.AllowInvalidCertificates, options.CaCertificatePath)})";
	}

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.Oof);
		SieveOofBackend oof = new(role.Settings.Bind<SieveOptions>(), role.Credentials, _wireLogger);
		return new BackendConnection([], oof: oof);
	}
}
