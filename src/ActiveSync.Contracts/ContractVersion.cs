// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Reflection;

namespace ActiveSync.Contracts;

/// <summary>
///   The version of the backend plugin contract — the public surface of THIS assembly, and of
///   nothing else. It once covered <c>ActiveSync.Protocol</c> too, back when the contract's own
///   signatures named EAS constants from it; no EAS wire encoding crosses the store boundary any
///   more, so Protocol is host-only, unpublished, and versions with the gateway release.
///   <para>
///     The value is READ FROM THIS ASSEMBLY rather than written here, because the single
///     definition lives in <c>Directory.Build.props</c> (<c>ContractVersion</c>) and is pinned
///     onto this project's <c>AssemblyVersion</c>. Raising it there raises it everywhere; there
///     is nothing to keep in sync by hand.
///   </para>
///   <para>
///     It is deliberately INDEPENDENT of the gateway's release version. The release tag flows
///     into every other assembly as <c>-p:Version</c>, but not into this one: otherwise
///     releasing the gateway as 2.0.0 for a product reason would flip the contract major and
///     refuse every existing plugin, having changed no API at all. The optional packages beside
///     the contract (<c>ActiveSync.Contracts.Interop</c>, <c>ActiveSync.Contracts.Conformance</c>)
///     are not loader ABI and take the release version instead, pinning the contract they were
///     built against as an exact dependency range.
///   </para>
///   <para>
///     <see cref="Major" /> AND <see cref="Minor" /> are both breaking: the loader requires a
///     plugin to declare an exact major.minor match (see
///     <see cref="SupportedGatewayContractAttribute" />). That is deliberate while the contract
///     is pre-2.0 and not ABI-stable — it lets an incompatible change land as a minor bump
///     instead of inflating the major into a meaningless counter. The patch component carries
///     non-surface fixes and is ignored by the gate.
///   </para>
/// </summary>
public static class ContractVersion
{
	// Not `const`: a const is baked into the CONSUMER at its own compile time, so a plugin
	// reading it would report the contract it was built against while appearing to report the
	// host's. Properties over a runtime-read value cannot lie about which one they are.
	private static readonly Version Assembly =
		ResolveAssemblyVersion(typeof(ContractVersion).Assembly.GetName().Version);

	/// <summary>
	///   This used to default to <c>new Version(1, 0)</c> when the assembly version was
	///   unreadable — but 1.0 is not a placeholder, it is a version that once shipped with a
	///   genuinely different surface (see <c>ContractSurface.approved.txt</c>), so defaulting to
	///   it would make the loader's gate silently ADMIT a stale plugin instead of refusing it. A
	///   defaulting security gate should default to refusing, so throw instead.
	///   <para>
	///     Unreachable in a normal build — the SDK always emits an <c>AssemblyVersion</c> — but
	///     reachable in principle under single-file/ILMerge/trimmed hosting or a rewritten
	///     assembly. Internal and parameterized so it can be exercised directly with a null input;
	///     the real call site can never observe a null in an ordinary test run.
	///   </para>
	/// </summary>
	internal static Version ResolveAssemblyVersion(Version? assemblyVersion) =>
		assemblyVersion ?? throw new InvalidOperationException(
			"ActiveSync.Contracts carries no assembly version; the plugin contract gate cannot be evaluated.");

	/// <summary>Breaking-change component. A plugin must declare this exact value.</summary>
	public static int Major => Assembly.Major;

	/// <summary>
	///   Also breaking, by current policy — a plugin must declare this exact value too.
	/// </summary>
	public static int Minor => Assembly.Minor;

	/// <summary>The contract version as a <see cref="System.Version" /> (Major.Minor).</summary>
	public static Version Current { get; } = new(Assembly.Major, Assembly.Minor);
}
