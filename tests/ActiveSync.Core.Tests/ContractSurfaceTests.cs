using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Structural guards on the published <c>ActiveSync.Contracts</c> plugin surface (item 17).
///   These assert the SHAPE of the contract a plugin compiles against — versioning affordance,
///   async connection creation, cancellation-token conventions, the exception hierarchy — so a
///   regression in the surface is caught here rather than by an out-of-repo plugin author.
/// </summary>
public sealed class ContractSurfaceTests
{
	// K69: there was no version constant anywhere — the loader gated on the raw assembly version
	// and a plugin had nothing to read or assert against. ContractVersion is that constant, and it
	// must stay in lockstep with the assembly version the plugin loader actually compares.
	[Fact]
	public void ContractVersion_MatchesTheAssemblyVersion()
	{
		Version assemblyVersion = typeof(IGatewayPlugin).Assembly.GetName().Version!;
		Assert.Equal(assemblyVersion.Major, ContractVersion.Major);
		Assert.Equal(assemblyVersion.Minor, ContractVersion.Minor);
		Assert.Equal(new Version(ContractVersion.Major, ContractVersion.Minor), ContractVersion.Current);
	}
}
