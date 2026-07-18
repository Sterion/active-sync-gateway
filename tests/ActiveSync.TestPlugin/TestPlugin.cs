using ActiveSync.Core.Backend;
using ActiveSync.Core.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.TestPlugin;

/// <summary>Plugin entry point — registers the fixture's backend provider.</summary>
public sealed class TestPlugin : IGatewayPlugin
{
	public void Register(IServiceCollection services, IConfiguration configuration)
	{
		services.AddSingleton<IBackendProvider, TestBackendProvider>();
	}
}

/// <summary>
///   A do-nothing provider named "testplugin" supporting the Notes role. CreateConnection is
///   never exercised by the loader tests — they only assert registration and type identity.
/// </summary>
public sealed class TestBackendProvider : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.Notes };

	public string Name => "testplugin";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings) => "test plugin provider";

	public IBackendConnection CreateConnection(BackendConnectionContext context) =>
		throw new NotSupportedException("The test plugin provider does not open connections.");
}
