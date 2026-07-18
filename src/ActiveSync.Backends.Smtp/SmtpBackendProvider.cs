using ActiveSync.Backends.Common;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Smtp;

/// <summary>The "smtp" provider: fills the MailSubmit role with <see cref="SmtpSubmitBackend" />.</summary>
public sealed class SmtpBackendProvider(ILoggerFactory loggerFactory) : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.MailSubmit };

	private readonly ILogger _logger = loggerFactory.CreateLogger<SmtpBackendProvider>();
	private readonly ILogger _wireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Smtp");

	public string Name => "smtp";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		SmtpOptions options = settings.Bind<SmtpOptions>();
		string context = $"smtp ({role})";
		BackendSettingsValidation.RequiredHost(options.Host, context, failures);
		BackendSettingsValidation.Port(options.Port, context, failures);
		BackendSettingsValidation.Choice(options.Security, "Security", context, failures,
			"None", "SslOnConnect", "StartTls", "StartTlsWhenAvailable", "Auto");
		BackendSettingsValidation.CaPath(options.CaCertificatePath, context, failures);
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		SmtpOptions options = settings.Bind<SmtpOptions>();
		return $"smtp {options.Host}:{options.Port} " +
		       $"(ssl={(options.UseSsl ? "on" : "off")}, security={options.Security ?? "auto"}, " +
		       $"forceFrom={(options.ForceFrom ? "on" : "off")}, " +
		       $"cert={BackendDescription.DescribeCert(options.AllowInvalidCertificates, options.CaCertificatePath)})";
	}

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.MailSubmit);
		SmtpSubmitBackend submit = new(
			role.Settings.Bind<SmtpOptions>(), role.Credentials, context.MailAddress, _logger, _wireLogger);
		return new BackendConnection([], submit);
	}
}
