namespace ActiveSync.Contracts;

/// <summary>
///   How a <see cref="BackendConfigField" /> is entered and validated. The rendering surface
///   (web UI, CLI) knows only these shapes — never a provider's option type.
/// </summary>
public enum BackendFieldType
{
	String,
	Int,
	Bool,
	Enum,

	/// <summary>
	///   A masked field: the rendering surface hides it, never echoes it back in an API response,
	///   and it is redacted from logs and the startup banner. K71: this governs RENDERING and
	///   REDACTION, not at-rest sealing — Contracts carries no crypto. The one secret the gateway
	///   seals in its state DB is the role's own credential (the Password), which the HOST seals and
	///   unseals and hands the provider in plaintext via <c>ResolvedRole.Credentials</c>. A provider
	///   that must seal an ADDITIONAL secret of its own references <c>ActiveSync.Crypto</c>
	///   (<c>SecretValue</c>) alongside Contracts — see docs/plugins.md.
	/// </summary>
	Secret,
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
/// <param name="SelfServiceEditable">
///   Whether a NON-ADMIN account holder may set this field for their own account from the user
///   portal. Defaults to <c>false</c>, so a field — and a whole plugin provider — is
///   administration-only until it says otherwise. Opt a field in only when changing it cannot
///   move the connection or weaken its trust: anything naming a host, URL, port, path template
///   or certificate policy decides WHERE the gateway connects and WHAT it will trust, and the
///   gateway presents the role's stored credential to whatever is there.
/// </param>
public sealed record BackendConfigField(
	string Name,
	string Label,
	BackendFieldType Type,
	bool Required = false,
	string? Default = null,
	IReadOnlyList<string>? EnumValues = null,
	string Help = "",
	long? Min = null,
	long? Max = null,
	bool SelfServiceEditable = false);
