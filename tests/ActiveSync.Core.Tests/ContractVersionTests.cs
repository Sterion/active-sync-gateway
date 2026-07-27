using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   K19 — <c>ContractVersion</c> silently reported 1.0 if the host assembly's version was ever
///   unreadable (<c>typeof(ContractVersion).Assembly.GetName().Version ?? new Version(1, 0)</c>),
///   which is the ONLY value <c>PluginLoader.VerifyDeclaredContract</c>/<c>VerifyContractVersions</c>
///   compare a plugin's declaration against. 1.0 is not a placeholder — it is a version that once
///   shipped with a genuinely different surface (a distinct hash is pinned for it in
///   <c>ContractSurface.approved.txt</c>) — so defaulting to it ADMITS a stale plugin instead of
///   refusing it. A defaulting security gate should default to refusing.
///   <para>
///     The real fallback is unreachable through <c>typeof(ContractVersion).Assembly</c> in a normal
///     test run — the SDK always emits an <c>AssemblyVersion</c>, so there is no way to make the
///     actual assembly report a null version without single-file/ILMerge/trimmed hosting or a
///     rewritten image. This exercises the extracted resolution logic directly with a null input
///     instead, which is the only way to observe the fallback deterministically — COVERAGE over
///     that logic, not a reproduction of the original unreadable-assembly symptom.
///   </para>
/// </summary>
public sealed class ContractVersionTests
{
	[Fact]
	public void ResolveAssemblyVersion_ThrowsInsteadOfDefaultingWhenTheVersionIsMissing()
	{
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
			ContractVersion.ResolveAssemblyVersion(null));
		Assert.Contains("assembly version", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ResolveAssemblyVersion_ReturnsThePresentVersionUnchanged()
	{
		Version version = new(3, 4);
		Assert.Same(version, ContractVersion.ResolveAssemblyVersion(version));
	}
}
