// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Reflection;

namespace ActiveSync.Contracts;

/// <summary>
///   The version of the backend plugin contract — the surface formed by
///   <c>ActiveSync.Contracts</c> and <c>ActiveSync.Protocol</c>, which version together.
///   <para>
///     The value is READ FROM THIS ASSEMBLY rather than written here, because the single
///     definition lives in <c>Directory.Build.props</c> (<c>ContractVersion</c>) and is pinned
///     onto both projects' <c>AssemblyVersion</c>. Raising it there raises it everywhere; there
///     is nothing to keep in sync by hand.
///   </para>
///   <para>
///     It is deliberately INDEPENDENT of the gateway's release version. The release tag flows
///     into every other assembly as <c>-p:Version</c>, but not into these two: otherwise
///     releasing the gateway as 2.0.0 for a product reason would flip the contract major and
///     refuse every existing plugin, having changed no API at all.
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
		typeof(ContractVersion).Assembly.GetName().Version ?? new Version(1, 0);

	/// <summary>Breaking-change component. A plugin must declare this exact value.</summary>
	public static int Major => Assembly.Major;

	/// <summary>
	///   Also breaking, by current policy — a plugin must declare this exact value too.
	/// </summary>
	public static int Minor => Assembly.Minor;

	/// <summary>The contract version as a <see cref="System.Version" /> (Major.Minor).</summary>
	public static Version Current { get; } = new(Assembly.Major, Assembly.Minor);
}
