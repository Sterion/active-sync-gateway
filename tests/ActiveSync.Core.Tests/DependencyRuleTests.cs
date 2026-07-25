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

	// S5 (round 2, reversing round-1 S4): MergedFreeBusy is host-only output — ResolveRecipientsHandler
	// calls MergedFreeBusy.Build, but a plugin only ever implements IFreeBusySource and returns
	// BusyPeriod; it never calls Build itself. Contracts is the published plugin-contract package and
	// must carry only what a plugin builds against (the same rule that already keeps IBackendSession /
	// BackendSessionFactory out of Contracts, per HostOnlySessionTypes_AreNotOnTheContractsSurface
	// above) — round 1's S4 moved it to Contracts on a different rationale (no EF/Core dependency) that
	// didn't account for the plugin-surface rule; this corrects it. BusyPeriod itself (the capability
	// model IFreeBusySource returns) stays in Contracts — Core already depends on Contracts, so it can
	// see BusyPeriod fine.
	[Fact]
	public void MergedFreeBusy_MovedFromContractsToCore()
	{
		System.Reflection.Assembly contracts = typeof(BusyPeriod).Assembly;
		System.Reflection.Assembly core = typeof(Core.Backend.BackendProviderRegistry).Assembly;

		Assert.Null(contracts.GetType("ActiveSync.Contracts.MergedFreeBusy"));
		Assert.NotNull(core.GetType("ActiveSync.Core.Backend.MergedFreeBusy"));
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

	// S7: the converter namespace (ActiveSync.Backends.Converters) was a second, unrelated root inside
	// the ActiveSync.Backends.Common assembly, alongside the assembly-named ActiveSync.Backends.Common
	// root everything else in the assembly uses — one assembly, two namespace roots, neither matching
	// the assembly name. Renaming it to ActiveSync.Backends.Common.Converters (folder-aligned, a child
	// of the assembly's own root) fixes that; this proves the old root is gone and the new one exists.
	[Fact]
	public void ConverterTypes_UseTheCommonAssemblyRootNamespace()
	{
		System.Reflection.Assembly common = typeof(MailKitWireLogger).Assembly;

		Assert.Null(common.GetType("ActiveSync.Backends.Converters.ContactConverter"));
		Assert.NotNull(common.GetType("ActiveSync.Backends.Common.Converters.ContactConverter"));
	}

	// S8 (round 1), narrowed by S7 (round 2): ActiveSync.Backends.Common is a published, plugin-facing
	// package. Its types must sit under the one assembly-named root, ActiveSync.Backends.Common —
	// S7 folded the former second root (ActiveSync.Backends.Converters) into it as a child namespace,
	// so a single StartsWith now covers both the helpers and the converters. ServerCertificateValidator
	// was the odd one out in the bare ActiveSync.Backends root — a namespace that conceptually belongs
	// to the sibling backend assemblies (Imap/Dav/…), forcing consumers to guess a third using for one
	// assembly. This guards against any type drifting back out of the one sanctioned root.
	[Fact]
	public void BackendsCommon_TypesUseCoherentNamespaces()
	{
		string[] offenders = typeof(MailKitWireLogger).Assembly
			.GetExportedTypes()
			.Where(static t => t.Namespace is null ||
				!t.Namespace.StartsWith("ActiveSync.Backends.Common", StringComparison.Ordinal))
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

	// S4: JmapMailStore.cs (847 lines, 26 async methods) was the one un-split backend store — the IMAP
	// equivalent already splits by concern (ImapMailBackend.Watch.cs), and JMAP itself already splits
	// calendar/contacts/oof/submit into their own types, just not mail. A compiled-assembly check can't
	// distinguish "one 847-line file" from "the same type spread across partial files" (the type and its
	// members are identical either way), so read the source layout directly — the way S1's csproj check
	// and S2's AesGcm-source check do for the same reason.
	[Fact]
	public void JmapMailStore_IsSplitIntoPartialFilesByConcern()
	{
		string dir = Path.Combine(FindRepoRoot(), "src", "ActiveSync.Backends.Jmap");

		Assert.True(File.Exists(Path.Combine(dir, "JmapMailStore.Search.cs")),
			"Expected JmapMailStore.Search.cs (the Email/query + Email/get search path) to exist as its own partial.");
		Assert.True(File.Exists(Path.Combine(dir, "JmapMailStore.Watch.cs")),
			"Expected JmapMailStore.Watch.cs (WaitForChangesAsync + folder-token polling) to exist as its own partial.");
		Assert.True(File.Exists(Path.Combine(dir, "JmapMailStore.Attachments.cs")),
			"Expected JmapMailStore.Attachments.cs (attachment fetch + file-reference codec) to exist as its own partial.");
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
