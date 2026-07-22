using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

/// <summary>
///   A14 — the schema-shape field validator. Scalar lookups used the caller-supplied dictionary's
///   comparer while list checks compared ordinal-ignore-case, so an entered key differing in case
///   from the field name ("host" vs "Host") was treated as unset and rejected as missing.
/// </summary>
public sealed class BackendConfigValidationTests
{
	private static readonly IReadOnlyList<BackendConfigField> Fields =
	[
		new BackendConfigField("Host", "Host", BackendFieldType.String, Required: true),
		new BackendConfigField("Port", "Port", BackendFieldType.Int),
	];

	[Fact]
	public void RequiredScalar_MatchesFieldName_CaseInsensitively()
	{
		// A case-SENSITIVE dictionary (ordinal) whose key differs in case from the field name — the
		// shape the web endpoint builds. It must still resolve the value, not report Host missing.
		Dictionary<string, string?> values = new(StringComparer.Ordinal)
		{
			["host"] = "imap.example",
			["port"] = "993",
		};

		IReadOnlyList<BackendFieldError> errors = BackendConfigValidation.ValidateFields(Fields, values);
		Assert.Empty(errors);
	}

	[Fact]
	public void GenuinelyMissingRequiredScalar_IsStillReported()
	{
		Dictionary<string, string?> values = new(StringComparer.Ordinal) { ["Port"] = "993" };
		IReadOnlyList<BackendFieldError> errors = BackendConfigValidation.ValidateFields(Fields, values);
		Assert.Contains(errors, e => e.Field == "Host");
	}
}
