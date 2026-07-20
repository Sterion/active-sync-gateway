namespace ActiveSync.Core.Backend;

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
public sealed record BackendConfigField(
	string Name,
	string Label,
	BackendFieldType Type,
	bool Required = false,
	string? Default = null,
	IReadOnlyList<string>? EnumValues = null,
	string Help = "",
	long? Min = null,
	long? Max = null);
