using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Contracts;

/// <summary>
///   Entry point of an out-of-repo plugin assembly dropped into the plugins directory. The
///   loader instantiates each public implementation (parameterless ctor) and calls
///   <see cref="Register" /> during service registration, so a plugin registers its backend
///   providers exactly like the in-repo ones — <c>services.AddSingleton&lt;IBackendProvider,
///   MyProvider&gt;()</c> — plus anything they depend on. The provider then serves any role
///   that config assigns to its <c>Name</c>.
/// </summary>
public interface IGatewayPlugin
{
	void Register(IServiceCollection services, IConfiguration configuration);
}
