// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;

namespace ActiveSync.Protocol;

/// <summary>
///   A parsed EAS protocol version ("14.1", "16.0", …), comparable so handlers can gate
///   16.x behavior with <c>context.Version >= EasVersion.V160</c>. Unparsable input maps
///   to <see cref="V141" /> — the wire default this gateway has always assumed.
/// </summary>
public readonly record struct EasVersion(int Major, int Minor) : IComparable<EasVersion>
{
	/// <summary>EAS protocol version 12.1. Parsed and advertised, but no longer offered as the newest option to clients.</summary>
	public static readonly EasVersion V121 = new(12, 1);

	/// <summary>EAS protocol version 14.0. Parsed and advertised alongside 14.1/16.x.</summary>
	public static readonly EasVersion V140 = new(14, 0);

	/// <summary>
	///   EAS protocol version 14.1 — the wire default this gateway assumes when a client omits
	///   or sends an unrecognized <c>MS-ASProtocolVersion</c> (see <see cref="Parse" />). Also the
	///   floor below which 16.x-only wire changes (e.g. <c>BodyPreference.Eas16</c>-gated behavior)
	///   must not be emitted, so 14.1 responses stay byte-identical to older observed behavior.
	/// </summary>
	public static readonly EasVersion V141 = new(14, 1);

	/// <summary>
	///   EAS protocol version 16.0 — the threshold checked by <c>context.Version >= EasVersion.V160</c>
	///   throughout the codebase to gate 16.x-only wire behavior (e.g. airsyncbase:Location(DisplayName)
	///   instead of calendar:Location, MERGE-not-clear exception date handling).
	/// </summary>
	public static readonly EasVersion V160 = new(16, 0);

	/// <summary>EAS protocol version 16.1, the newest version this gateway implements and advertises.</summary>
	public static readonly EasVersion V161 = new(16, 1);

	/// <summary>
	///   The EAS protocol versions this gateway recognizes (2.5 / 12.0 are parsed but no longer
	///   advertised — see EasEndpoint). The <c>MS-ASProtocolVersion</c> header is unauthenticated
	///   client input and the parsed version gates 16.x behaviour, so <see cref="Parse" /> matches
	///   against this set rather than trusting arbitrary major/minor: a header of "99.9" used to
	///   yield <c>EasVersion(99, 9)</c>, clearing every <c>&gt;= V160</c> / <c>&gt;= V161</c> check —
	///   the same hole the base64-query <c>ProtocolVersionBytes</c> allowlist already closed one
	///   field over.
	/// </summary>
	private static readonly EasVersion[] Known = [new(2, 5), new(12, 0), V121, V140, V141, V160, V161];

	/// <summary>
	///   Parses an <c>MS-ASProtocolVersion</c> header value (e.g. <c>"16.1"</c>) against the
	///   <see cref="Known" /> allowlist. A <see langword="null" /> value, text that is not a strict
	///   unsigned "major.minor" pair, or a major/minor combination this gateway does not recognize
	///   all fall back to <see cref="V141" /> rather than yielding an arbitrary parsed version —
	///   this keeps unauthenticated client input from spoofing a version high enough to clear
	///   <c>&gt;= V160</c> / <c>&gt;= V161</c> gates.
	/// </summary>
	/// <param name="value">The raw header value to parse, or <see langword="null" />.</param>
	/// <returns>The matching known <see cref="EasVersion" />, or <see cref="V141" /> if <paramref name="value" /> does not match one.</returns>
	public static EasVersion Parse(string? value)
	{
		if (value is null)
			return V141;
		int dot = value.IndexOf('.');
		// NumberStyles.None + InvariantCulture (rather than the default
		// NumberStyles.Integer + CurrentCulture) so whitespace- or sign-padded text ("
		// 16.1", "+16.+1") does not parse -- the Known allowlist below is meant to gate the
		// literal wire text, not a culture-tolerant reading of it.
		if (dot <= 0 ||
		    !int.TryParse(value.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
		    !int.TryParse(value.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
			return V141;
		EasVersion parsed = new(major, minor);
		return Array.IndexOf(Known, parsed) >= 0 ? parsed : V141;
	}

	/// <summary>Compares versions by <see cref="Major" /> first, then <see cref="Minor" /> — the ordering the <c>&lt;</c>/<c>&gt;</c>/<c>&lt;=</c>/<c>&gt;=</c> operators build on.</summary>
	/// <param name="other">The version to compare against.</param>
	/// <returns>A negative value if this version precedes <paramref name="other" />, zero if equal, a positive value if it follows.</returns>
	public int CompareTo(EasVersion other)
	{
		int major = Major.CompareTo(other.Major);
		return major != 0 ? major : Minor.CompareTo(other.Minor);
	}

	/// <summary>Returns whether <paramref name="left" /> denotes an older protocol version than <paramref name="right" />.</summary>
	/// <param name="left">The left-hand version.</param>
	/// <param name="right">The right-hand version.</param>
	/// <returns><see langword="true" /> if <paramref name="left" /> precedes <paramref name="right" />.</returns>
	public static bool operator <(EasVersion left, EasVersion right) => left.CompareTo(right) < 0;

	/// <summary>Returns whether <paramref name="left" /> denotes a newer protocol version than <paramref name="right" />.</summary>
	/// <param name="left">The left-hand version.</param>
	/// <param name="right">The right-hand version.</param>
	/// <returns><see langword="true" /> if <paramref name="left" /> follows <paramref name="right" />.</returns>
	public static bool operator >(EasVersion left, EasVersion right) => left.CompareTo(right) > 0;

	/// <summary>Returns whether <paramref name="left" /> is the same protocol version as, or older than, <paramref name="right" />. This is the shape of the <c>context.Version >= EasVersion.V160</c>-style gates used throughout the handlers (via the mirrored <c>&gt;=</c> operator).</summary>
	/// <param name="left">The left-hand version.</param>
	/// <param name="right">The right-hand version.</param>
	/// <returns><see langword="true" /> if <paramref name="left" /> does not follow <paramref name="right" />.</returns>
	public static bool operator <=(EasVersion left, EasVersion right) => left.CompareTo(right) <= 0;

	/// <summary>Returns whether <paramref name="left" /> is the same protocol version as, or newer than, <paramref name="right" /> — the operator behind version gates such as <c>context.Version >= EasVersion.V160</c>.</summary>
	/// <param name="left">The left-hand version.</param>
	/// <param name="right">The right-hand version.</param>
	/// <returns><see langword="true" /> if <paramref name="left" /> does not precede <paramref name="right" />.</returns>
	public static bool operator >=(EasVersion left, EasVersion right) => left.CompareTo(right) >= 0;

	/// <summary>Formats the version as the wire-style <c>"major.minor"</c> string (e.g. <c>"16.1"</c>) used in the <c>MS-ASProtocolVersion</c> header.</summary>
	/// <returns>The <c>"{Major}.{Minor}"</c> representation of this version.</returns>
	public override string ToString() => $"{Major}.{Minor}";
}
