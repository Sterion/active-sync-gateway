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

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.MailSubmit);
		SmtpSubmitBackend submit = new(
			(SmtpOptions)role.Settings!, role.Credentials, context.MailAddress, _logger, _wireLogger);
		return new BackendConnection([], submit);
	}
}
