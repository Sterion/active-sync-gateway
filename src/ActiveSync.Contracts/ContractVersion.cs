namespace ActiveSync.Contracts;

/// <summary>
///   The version of the backend plugin contract — the <c>ActiveSync.Contracts</c> surface a
///   plugin compiles against. The contract is NOT ABI-stable before 2.0 (see docs/plugins.md):
///   the host plugin loader refuses any assembly in a plugin folder whose referenced
///   <c>ActiveSync.Contracts</c> <see cref="Major" /> differs from the host's.
///   <para>
///     Bump <see cref="Major" /> on any breaking change to the surface, <see cref="Minor" /> on
///     an additive one, and keep this in lockstep with the assembly version — a test
///     (<c>ContractSurfaceTests.ContractVersion_MatchesTheAssemblyVersion</c>) asserts they agree,
///     so the constant a plugin can read at runtime and the version the loader actually gates on
///     cannot drift apart.
///   </para>
/// </summary>
public static class ContractVersion
{
	/// <summary>Breaking-change component. Must match the host for a plugin to load.</summary>
	public const int Major = 1;

	/// <summary>Additive-change component.</summary>
	public const int Minor = 0;

	/// <summary>The contract version as a <see cref="System.Version" /> (Major.Minor).</summary>
	public static Version Current { get; } = new(Major, Minor);
}
