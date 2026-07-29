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
///   A NON-public plugin entry point. The loader's error message promises it looks for a
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

	public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
		throw new NotSupportedException("The internal test plugin provider does not open connections.");
}

/// <summary>
///   A WORKING provider named "testplugin" serving the Notes role over
///   <see cref="TestNotesStore" />. It used to throw from <c>CreateConnectionAsync</c>, which made
///   the fixture prove only that a plugin can register — never that one can actually sync. The
///   conformance kit runs against the store this returns, so "ActiveSync.Contracts alone is enough
///   to write a backend" is tested rather than asserted.
/// </summary>
public sealed class TestBackendProvider : IBackendProvider
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.Notes };

	public string Name => "testplugin";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
	}

	// Names the copy of the plugin's private dependency this provider bound to.
	public string DescribeRole(BackendRole role, ProviderSettings settings) =>
		$"test plugin provider (dep: {PluginPrivateLib.PrivateDependency.LoadedFrom})";

	// One store per account connection, held by the connection and disposed with it — the shape
	// every in-repo provider uses. Nothing here needs a transport, so there is nothing to await.
	public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
		Task.FromResult<IBackendConnection>(new BackendConnection([new TestNotesStore()]));
}
