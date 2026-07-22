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
		Assert.Equal(ContractVersion.Major, assemblyVersion.Major);
		Assert.Equal(ContractVersion.Minor, assemblyVersion.Minor);
		Assert.Equal(new Version(ContractVersion.Major, ContractVersion.Minor), ContractVersion.Current);
	}

	// K67: BackendItemNotFoundException derived straight from Exception, so the codebase-wide
	// `catch (BackendException)` idiom silently MISSED it — an item-gone thrown from a store
	// escaped every handler written to funnel backend errors. It must be a BackendException. And
	// BackendException was sealed, so a plugin could not introduce its own typed backend error;
	// unseal it.
	[Fact]
	public void BackendItemNotFoundException_IsABackendException()
	{
		Assert.True(typeof(BackendException).IsAssignableFrom(typeof(BackendItemNotFoundException)));

		BackendException? caught = null;
		try
		{
			throw new BackendItemNotFoundException("gone");
		}
		catch (BackendException ex)
		{
			caught = ex;
		}

		Assert.IsType<BackendItemNotFoundException>(caught);
	}

	[Fact]
	public void BackendException_IsNotSealed_SoPluginsCanAddTypedErrors()
	{
		Assert.False(typeof(BackendException).IsSealed);
	}

	// K61: CreateConnection was synchronous in an otherwise fully async contract — a provider that
	// opens a TCP/TLS connection could not do it without blocking. It is now
	// Task<IBackendConnection> CreateConnectionAsync(context, ct); the synchronous method is gone.
	[Fact]
	public void CreateConnection_IsAsync_WithCancellationTokenLast()
	{
		Assert.Null(typeof(IBackendProvider).GetMethod("CreateConnection"));

		MethodInfo method = typeof(IBackendProvider).GetMethod("CreateConnectionAsync")!;
		Assert.Equal(typeof(Task<IBackendConnection>), method.ReturnType);
		Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);
	}

	// K57: the published plugin surface (ActiveSync.Contracts) must carry only what a plugin
	// actually builds against. IBackendSession / IBackendSessionFactory / BackendSessionInfo are
	// the HOST's composite session, its cache/factory and the dashboard projection of that cache —
	// nothing a plugin implements or receives — so they must NOT be exported by Contracts. They now
	// live in ActiveSync.Core.Backend. (Structural guard, in the spirit of items 15/16.)
	[Theory]
	[InlineData("IBackendSession")]
	[InlineData("IBackendSessionFactory")]
	[InlineData("BackendSessionInfo")]
	public void HostOnlySessionTypes_AreNotOnTheContractsSurface(string typeName)
	{
		string[] contractsExports = typeof(IContentStore).Assembly
			.GetExportedTypes()
			.Select(static t => t.Name)
			.ToArray();
		Assert.DoesNotContain(typeName, contractsExports);
	}

	[Fact]
	public void HostSessionTypes_LiveInCore()
	{
		Assert.Equal("ActiveSync.Core.Backend", typeof(Core.Backend.IBackendSession).Namespace);
		Assert.Equal("ActiveSync.Core.Backend", typeof(Core.Backend.IBackendSessionFactory).Namespace);
		Assert.Equal("ActiveSync.Core.Backend", typeof(Core.Backend.BackendSessionInfo).Namespace);
	}

	// K59: DeleteItemAsync put the CancellationToken THIRD (before `bool permanent = false`),
	// breaking the ct-last convention every other member honours, and used an optional parameter
	// on an interface method (a compile-time default the implementer cannot see or change). The
	// token must come last and there must be no optional parameters.
	[Fact]
	public void DeleteItemAsync_TakesCancellationTokenLast_WithNoOptionalParameters()
	{
		MethodInfo method = typeof(IContentStore).GetMethod(nameof(IContentStore.DeleteItemAsync))!;
		ParameterInfo[] parameters = method.GetParameters();

		Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
		Assert.DoesNotContain(parameters, static p => p.IsOptional);
	}
}
