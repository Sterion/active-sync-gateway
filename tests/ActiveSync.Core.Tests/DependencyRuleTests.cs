using System.Linq;

using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using ActiveSync.Crypto;

namespace ActiveSync.Core.Tests;

public sealed class DependencyRuleTests
{
	// S2 / K49: the ActiveSync.Crypto assembly is a published contract package that the slim `eas`
	// client references INSTEAD of Core (its BCL-only start is the whole point). Types that shipped
	// in this assembly while declaring ActiveSync.Core.* namespaces made the "doesn't reference Core"
	// property invisible in the client's own source and invited a genuine Core using to slip in
	// unnoticed behind a namespace that reads like Core's. Every public type the assembly exports
	// must sit under its own ActiveSync.Crypto root namespace.
	[Fact]
	public void Crypto_TypesDeclareTheCryptoNamespace()
	{
		string[] offenders = typeof(LocalCliEnvelope).Assembly
			.GetExportedTypes()
			.Where(static t => t.Namespace is null ||
				!t.Namespace.StartsWith("ActiveSync.Crypto", StringComparison.Ordinal))
			.Select(static t => t.FullName!)
			.OrderBy(static n => n, StringComparer.Ordinal)
			.ToArray();

		Assert.Empty(offenders);
	}

	// S1: ActiveSync.Backends.Common is a published, plugin-facing package. A plugin references
	// ActiveSync.Contracts (plus Common only for the converters) — never ActiveSync.Core, the
	// host graph. Common used all of Core for a single WireLog.Payload call; once WireLog and
	// TransientRetry move to Contracts the reference is gone, and this guards it from creeping
	// back via a stray using or a Core helper call. (The broad plugin-boundary suite is item 44 /
	// S5; this is the narrow, finding-specific proof for S1.)
	[Fact]
	public void BackendsCommon_DoesNotReferenceCore()
	{
		string[] referenced = typeof(MailKitWireLogger).Assembly
			.GetReferencedAssemblies()
			.Select(static a => a.Name!)
			.ToArray();

		Assert.DoesNotContain("ActiveSync.Core", referenced);
	}

	// S4: MergedFreeBusy builds the MS-ASCMD digit string — pure protocol logic with no EF/Core-state
	// dependency, so it does not belong in Core. Its one project dependency is BusyPeriod, a Contracts
	// capability model, so it moves to Contracts (the lowest layer the dependency rule permits it to
	// sit in). The finding named ActiveSync.Protocol, but Protocol references nothing project-wise and
	// cannot see BusyPeriod; relocating BusyPeriod there would be a breaking plugin-contract change
	// owned by item 17. Contracts honours the finding's intent — out of Core, into a fuzzable leaf.
	[Fact]
	public void MergedFreeBusy_MovedFromCoreToContracts()
	{
		System.Reflection.Assembly core = typeof(Core.Backend.BackendProviderRegistry).Assembly;
		System.Reflection.Assembly contracts = typeof(BusyPeriod).Assembly;

		Assert.Null(core.GetType("ActiveSync.Core.Backend.MergedFreeBusy"));
		Assert.NotNull(contracts.GetType("ActiveSync.Contracts.MergedFreeBusy"));
	}

	// S4: CollectionDiff is the differential-sync windowing algorithm — pure protocol logic depending
	// on nothing but BCL types and its own records. It belongs in ActiveSync.Protocol, where it is also
	// easier to fuzz in isolation from the Core host graph.
	[Fact]
	public void CollectionDiff_MovedFromCoreToProtocol()
	{
		System.Reflection.Assembly core = typeof(Core.Backend.BackendProviderRegistry).Assembly;
		System.Reflection.Assembly protocol = typeof(ActiveSync.Protocol.Wbxml.WbxmlEncoder).Assembly;

		Assert.Null(core.GetType("ActiveSync.Core.Sync.CollectionDiff"));
		Assert.NotNull(protocol.GetType("ActiveSync.Protocol.Sync.CollectionDiff"));
	}
}
