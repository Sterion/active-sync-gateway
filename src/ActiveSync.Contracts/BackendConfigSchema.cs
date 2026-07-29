// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   How a <see cref="BackendConfigField" /> is entered and validated. The rendering surface
///   (web UI, CLI) knows only these shapes — never a provider's option type.
/// </summary>
public enum BackendFieldType
{
	/// <summary>Free-text value, rendered as a plain text input.</summary>
	String,

	/// <summary>Whole-number value; <see cref="BackendConfigField.Min" />/<see cref="BackendConfigField.Max" /> bound it when set.</summary>
	Int,

	/// <summary>Boolean flag, rendered as a checkbox/toggle.</summary>
	Bool,

	/// <summary>A closed set of choices; <see cref="BackendConfigField.EnumValues" /> lists the allowed values.</summary>
	Enum,

	/// <summary>
	///   A masked field: the rendering surface hides it, never echoes it back in an API response,
	///   and it is redacted from logs and the startup banner. This governs RENDERING and
	///   REDACTION, not at-rest sealing — Contracts carries no crypto. The one secret the gateway
	///   seals in its state DB is the role's own credential (the Password), which the HOST seals and
	///   unseals and hands the provider in plaintext via <c>ResolvedRole.Credentials</c>. A provider
	///   that must seal an ADDITIONAL secret of its own references <c>ActiveSync.Crypto</c>
	///   (<c>SecretValue</c>) alongside Contracts — see docs/plugins.md.
	/// </summary>
	Secret,

	/// <summary>An absolute URL string, rendered as a text input (no scheme/host validation implied).</summary>
	Url,

	/// <summary>Repeated element; the field Name is the list ROOT ("X" for the keys "X:0", "X:1").</summary>
	StringList
}

/// <summary>
///   One configuration leaf a provider reads for a role, described well enough for a form to
///   be rendered without knowing the provider: label, shape, default, allowed values, help.
///   <see cref="Name" /> is the config key relative to the role section ("Host"); for
///   <see cref="BackendFieldType.StringList" /> it is the list root ("SharedCollections").
///   <see cref="Default" /> is the string form of the options-class default and MUST match it
///   (BackendSchemaDefaultsTests binds an empty section and compares).
/// </summary>
public sealed record BackendConfigField
{
	/// <summary>
	///   The config key relative to the role section (e.g. "Host"), or the list ROOT for
	///   <see cref="BackendFieldType.StringList" /> (e.g. "SharedCollections" for keys
	///   "SharedCollections:0", "SharedCollections:1", …).
	/// </summary>
	public required string Name { get; init; }

	/// <summary>Short human-readable label for the rendering surface.</summary>
	public required string Label { get; init; }

	/// <summary>The field's shape — governs how it is entered, validated and rendered.</summary>
	public required BackendFieldType Type { get; init; }

	/// <summary>Whether a value must be supplied; unset otherwise falls back to <see cref="Default" />.</summary>
	public bool Required { get; init; }

	/// <summary>
	///   The string form of the options-class property's own default value. MUST match it exactly —
	///   <c>BackendSchemaDefaultsTests</c> binds an empty section and compares, so a drift here renders
	///   a wrong "(default: X)" hint to the operator.
	/// </summary>
	public string? Default { get; init; }

	/// <summary>The allowed values when <see cref="Type" /> is <see cref="BackendFieldType.Enum" />; otherwise unused.</summary>
	public IReadOnlyList<string>? EnumValues { get; init; }

	/// <summary>Longer help text shown alongside the field in the rendering surface.</summary>
	public string Help { get; init; } = "";

	/// <summary>Inclusive lower bound for <see cref="BackendFieldType.Int" /> fields; otherwise unused.</summary>
	public long? Min { get; init; }

	/// <summary>Inclusive upper bound for <see cref="BackendFieldType.Int" /> fields; otherwise unused.</summary>
	public long? Max { get; init; }

	/// <summary>
	///   Whether a NON-ADMIN account holder may set this field for their own account from the user
	///   portal. Defaults to <c>false</c>, so a field — and a whole plugin provider — is
	///   administration-only until it says otherwise. Opt a field in only when changing it cannot
	///   move the connection or weaken its trust: anything naming a host, URL, port, path template
	///   or certificate policy decides WHERE the gateway connects and WHAT it will trust, and the
	///   gateway presents the role's stored credential to whatever is there.
	/// </summary>
	public bool SelfServiceEditable { get; init; }
}
