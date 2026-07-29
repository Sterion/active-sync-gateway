using ActiveSync.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   The plugin entry point the loader looks for: one public <see cref="IGatewayPlugin" /> whose
///   only job is registering the provider, exactly as an in-repo backend is registered in the
///   host's own service collection.
/// </summary>
public sealed class LocalFilesPlugin : IGatewayPlugin
{
	/// <inheritdoc />
	public void Register(IServiceCollection services, IConfiguration configuration)
	{
		services.AddSingleton<IBackendProvider, LocalFilesBackendProvider>();
	}
}
