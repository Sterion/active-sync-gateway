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

	// S4: MergedFreeBusy_MovedFromCoreToContracts (round-1 guard) is replaced in the S5 block below —
	// item 17 (round 2) moves MergedFreeBusy back to Core; see MergedFreeBusy_MovedFromContractsToCore.

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

	// S9 (round 2): WireLog is BCL-only and a plugin never calls it — Backends.Common and Server use
	// it to sanitize wire-log dumps, but nothing in the published plugin contract references it. It
	// shrinks the Contracts surface by moving to ActiveSync.Protocol (below Contracts, where Protocol's
	// "depends on nothing project-wise" rule already fits a BCL-only string helper).
	[Fact]
	public void WireLog_MovedFromContractsToProtocol()
	{
		System.Reflection.Assembly contracts = typeof(BusyPeriod).Assembly;
		System.Reflection.Assembly protocol = typeof(ActiveSync.Protocol.Wbxml.WbxmlEncoder).Assembly;

		Assert.Null(contracts.GetType("ActiveSync.Contracts.WireLog"));
		Assert.NotNull(protocol.GetType("ActiveSync.Protocol.WireLog"));
	}

	// S8: ActiveSync.Backends.Common is a published, plugin-facing package. Its types must sit under a
	// coherent namespace set — the assembly-named ActiveSync.Backends.Common (its helpers) or the
	// purpose-named ActiveSync.Backends.Converters (the EAS converters). ServerCertificateValidator was
	// the odd one out in the bare ActiveSync.Backends root — a namespace that conceptually belongs to
	// the sibling backend assemblies (Imap/Dav/…), forcing consumers to guess a third using for one
	// assembly. This guards against any type drifting back out of the two sanctioned namespaces.
	[Fact]
	public void BackendsCommon_TypesUseCoherentNamespaces()
	{
		string[] offenders = typeof(MailKitWireLogger).Assembly
			.GetExportedTypes()
			.Where(static t => t.Namespace is null ||
				!(t.Namespace.StartsWith("ActiveSync.Backends.Common", StringComparison.Ordinal) ||
					t.Namespace.StartsWith("ActiveSync.Backends.Converters", StringComparison.Ordinal)))
			.Select(static t => t.FullName!)
			.OrderBy(static n => n, StringComparer.Ordinal)
			.ToArray();

		Assert.Empty(offenders);
	}

	// S1 (round 2): Server references Core pervasively (CliServices, BackendProviderRegistry, ...) but
	// only ever picked it up TRANSITIVELY via its Backends/WebUi ProjectReferences — the compiled
	// assembly's reference list looks identical either way (MSBuild flows every transitive project
	// output onto the compile path, so a reflection-based "does Server.dll reference Core.dll" check
	// can't distinguish an explicit reference from an accidental one). The only place the distinction is
	// visible is the csproj itself: a Backends project quietly dropping its own Core reference would
	// break Server's build for a reason nothing in Server's own file explains. Read the file directly.
	[Fact]
	public void Server_HasExplicitProjectReferenceToCore()
	{
		string csproj = File.ReadAllText(
			Path.Combine(FindRepoRoot(), "src", "ActiveSync.Server", "ActiveSync.Server.csproj"));

		Assert.Contains("ActiveSync.Core.csproj", csproj);
	}

	// S2 / K11: SecretValue (Crypto) and LocalContentProtector (Core/Security) each hand-roll the
	// identical AES-256-GCM nonce‖ct‖tag framing independently — same NonceSize/TagSize,
	// RandomNumberGenerator.Fill + `using AesGcm aes = new(key, TagSize)` + base64+prefix
	// seal/unseal, differing only in prefix and AAD. A framing fix (constant-time handling, a v2
	// format, a nonce-reuse audit) would have to land in both assemblies or silently diverge. Both
	// callers must delegate to one shared primitive rather than constructing AesGcm themselves —
	// read the source directly, the way S1's csproj check does, since a compiled-assembly check
	// can't distinguish "shares an AesGcm call" from "shares an AesGcm framing implementation".
	[Fact]
	public void SecretValueAndLocalContentProtector_DoNotConstructAesGcmThemselves()
	{
		string repoRoot = FindRepoRoot();
		string secretValueSource = File.ReadAllText(
			Path.Combine(repoRoot, "src", "ActiveSync.Crypto", "SecretValue.cs"));
		string protectorSource = File.ReadAllText(
			Path.Combine(repoRoot, "src", "ActiveSync.Core", "Security", "LocalContentProtector.cs"));

		Assert.DoesNotContain("AesGcm aes = new", secretValueSource);
		Assert.DoesNotContain("AesGcm aes = new", protectorSource);
	}

	private static string FindRepoRoot()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ActiveSync.slnx")))
			dir = dir.Parent;
		return dir?.FullName
			?? throw new InvalidOperationException("Could not locate repo root (ActiveSync.slnx) above the test binary.");
	}
}
