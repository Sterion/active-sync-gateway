using ActiveSync.Server.Eas;
using ActiveSync.Server.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E4 turned per-request handler dispatch from "construct all 19 and pick one" into keyed
///   resolution of exactly one. That only works if three things stay in lock-step: the DI key,
///   the handler's own <see cref="IEasCommandHandler.Command" />, and the endpoint's advertised
///   command set. A drift between any two is silent in the build but shows up as a 501 for a
///   real device — so lock them here. (Coverage for a behaviour-preserving refactor; there is no
///   pre-existing defect to reproduce red-first.)
/// </summary>
public sealed class EasHandlerRegistrationTests
{
	[Fact]
	public void EveryHandlerIsRegisteredKeyed_ByItsCommandName()
	{
		ServiceCollection services = new();
		services.AddEasHandlers();

		List<ServiceDescriptor> keyed = services
			.Where(d => d.ServiceType == typeof(IEasCommandHandler) && d.IsKeyedService)
			.ToList();

		// Exactly one keyed registration per command, no non-keyed leftovers (which would
		// re-introduce the all-handlers enumeration the fix removed).
		Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEasCommandHandler) && !d.IsKeyedService);

		foreach ((string command, Type handler) in ServiceCollectionExtensions.EasHandlerRegistrations)
		{
			ServiceDescriptor descriptor = Assert.Single(keyed, d => Equals(d.ServiceKey, command));
			Assert.Equal(handler, descriptor.KeyedImplementationType);
			Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
		}
	}

	[Fact]
	public void EachHandlerType_ReportsTheCommandItIsKeyedUnder()
	{
		// The key must equal the handler's own Command, or the endpoint's canonical-command
		// dispatch resolves the wrong type. Every Command getter is a field-free literal
		// (`=> "Sync"`), so an uninitialized instance yields it without constructing dependencies.
		foreach ((string command, Type handler) in ServiceCollectionExtensions.EasHandlerRegistrations)
		{
			IEasCommandHandler instance = (IEasCommandHandler)
				System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(handler);
			Assert.Equal(command, instance.Command);
		}
	}

	[Fact]
	public void RegisteredCommands_MatchTheAdvertisedProtocolCommandSet()
	{
		HashSet<string> registered = ServiceCollectionExtensions.EasHandlerRegistrations
			.Select(r => r.Command).ToHashSet(StringComparer.Ordinal);
		HashSet<string> advertised = EasEndpoint.AdvertisedCommands.ToHashSet(StringComparer.Ordinal);
		Assert.Equal(advertised, registered);
	}
}
