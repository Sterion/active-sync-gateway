using System.Linq;

using ActiveSync.Backends.Common;

namespace ActiveSync.Core.Tests;

public sealed class DependencyRuleTests
{
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
}
