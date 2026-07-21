using System.Security.Cryptography;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Configuration;

namespace ActiveSync.Core.Options;

/// <summary>
///   Startup validation of the ActiveSync:Backends role sections and the declared users —
///   runs AFTER the service provider is built (unlike <see cref="ActiveSyncOptionsValidator" />)
///   because it needs the provider registry: every named provider must exist and support its
///   role, and each provider validates its own settings (plugins included).
/// </summary>
public sealed class BackendConfigurationValidator(
	Microsoft.Extensions.Options.IOptions<ActiveSyncOptions> options,
	IConfiguration configuration,
	BackendProviderRegistry registry)
{
	/// <summary>Throws with every failure listed when the backend configuration is invalid.</summary>
	public void Validate()
	{
		List<string> failures = new();
		BackendRolesConfig roles = BackendRolesConfig.Load(configuration, failures);

		foreach ((BackendRole role, RoleAssignment assignment) in roles.Assignments)
			try
			{
				registry.GetFor(assignment.ProviderName, role)
					.ValidateConfiguration(role, assignment.Settings, failures);
			}
			catch (InvalidOperationException ex)
			{
				failures.Add($"ActiveSync:Backends:{role}: {ex.Message}");
			}

		if (options.Value.Users is { Count: > 0 })
		{
			// Pass the key only if it loads — key problems are already reported by the
			// options validator, and any enc: values are then flagged as unresolvable.
			byte[]? key = EncryptionKeyLoader.TryLoadKey(options.Value.Encryption, out string? _);
			AccountResolver.ValidateUsers(options.Value, roles, registry, key, failures);
			if (key is not null)
				CryptographicOperations.ZeroMemory(key);
		}

		if (failures.Count > 0)
			throw new InvalidOperationException(
				"Backend configuration is invalid:" + Environment.NewLine +
				string.Join(Environment.NewLine, failures));
	}
}
