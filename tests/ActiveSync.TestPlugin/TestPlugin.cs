using ActiveSync.Contracts;
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
///   K40 — a NON-public plugin entry point. The loader's error message promises it looks for a
///   public <see cref="IGatewayPlugin" />, so this type must be ignored; it registers a provider
///   under its own name purely so a test can assert the provider never appears.
/// </summary>
internal sealed class InternalTestPlugin : IGatewayPlugin
{
	public void Register(IServiceCollection services, IConfiguration configuration)
	{
		services.AddSingleton<IBackendProvider, InternalTestBackendProvider>();
	}
}

/// <summary>The provider <see cref="InternalTestPlugin" /> would register if it were loaded.</summary>
internal sealed class InternalTestBackendProvider : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.Notes };

	public string Name => "internal-testplugin";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings) => "internal test plugin provider";

	public IBackendConnection CreateConnection(BackendConnectionContext context) =>
		throw new NotSupportedException("The internal test plugin provider does not open connections.");
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

	// Names the copy of the plugin's private dependency this provider bound to (K41).
	public string DescribeRole(BackendRole role, ProviderSettings settings) =>
		$"test plugin provider (dep: {PluginPrivateLib.PrivateDependency.LoadedFrom})";

	public IBackendConnection CreateConnection(BackendConnectionContext context) =>
		throw new NotSupportedException("The test plugin provider does not open connections.");
}
