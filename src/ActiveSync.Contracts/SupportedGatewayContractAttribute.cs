// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   Declares which gateway plugin contract a plugin assembly supports. Apply it once, to the
///   plugin's ENTRY assembly:
///   <code>[assembly: SupportedGatewayContract(1, 0)]</code>
///   <para>
///     This is a DECLARATION, not an inference. A plugin's own version is its own business — a
///     plugin may be at 3.7.2 and support contract 1.0 — and which package version it happened
///     to compile against says nothing about which contract its author verified it against.
///     Only the author knows that, so only the author states it.
///   </para>
///   <para>
///     It is an assembly attribute rather than a member of <see cref="IGatewayPlugin" /> because
///     the loader must check compatibility BEFORE loading anything: asking an object for its
///     supported version means instantiating a type from an assembly that may be incompatible,
///     which is the <c>TypeLoadException</c>-deep-inside-a-sync failure the check exists to
///     prevent. An attribute can be read from metadata with nothing loaded.
///   </para>
///   <para>
///     Both components must match the host exactly (see <see cref="ContractVersion" />); the
///     patch component is not part of the declaration because it never breaks anything.
///   </para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class SupportedGatewayContractAttribute(int major, int minor) : Attribute
{
	/// <summary>Contract major version this assembly was written and verified against.</summary>
	public int Major { get; } = major;

	/// <summary>Contract minor version this assembly was written and verified against.</summary>
	public int Minor { get; } = minor;
}
