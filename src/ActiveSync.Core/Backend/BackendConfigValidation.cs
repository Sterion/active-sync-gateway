using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

/// <summary>One rejected value, attributable to a field so a form can mark the offending input.</summary>
public sealed record BackendFieldError(string? Field, string Message);

/// <summary>
///   Generic, provider-agnostic checks derived from a provider's <see cref="BackendConfigField" />
///   schema: what a form can reject before anything is stored. Semantic checks stay with the
///   provider (<see cref="IBackendProvider.ValidateConfiguration" />) — this only enforces the
///   shape the schema itself declares, so the same rules apply to the web UI and the CLI.
/// </summary>
public static class BackendConfigValidation
{
	/// <summary>"SharedCollections:0" → "SharedCollections"; "Host" → "Host".</summary>
	public static string ListRoot(string key)
	{
		while (true)
		{
			int separator = key.LastIndexOf(':');
			if (separator < 0 || !int.TryParse(key[(separator + 1)..], out int _))
				return key;
			key = key[..separator];
		}
	}

	/// <summary>
	///   Checks entered leaf values against the schema. Values not claimed by any field pass
	///   untouched — plugin providers describing nothing (or partially) must stay configurable.
	/// </summary>
	public static IReadOnlyList<BackendFieldError> ValidateFields(
		IReadOnlyList<BackendConfigField> fields, IReadOnlyDictionary<string, string?> values)
	{
		// Configuration keys are case-insensitive, but this dictionary's comparer depends on the
		// caller (the web endpoint builds it from a request body). A scalar lookup below used the
		// caller's comparer while the list check compared ordinal-ignore-case, so "host=" against a
		// field "Host" was treated as unset and rejected as missing (A14). Normalize once here so
		// both paths agree on ordinal-ignore-case.
		Dictionary<string, string?> lookup = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string key, string? value) in values)
			lookup[key] = value;

		List<BackendFieldError> errors = [];
		foreach (BackendConfigField field in fields)
		{
			if (field.Type == BackendFieldType.StringList)
			{
				bool anyElement = lookup.Any(pair =>
					ListRoot(pair.Key).Equals(field.Name, StringComparison.OrdinalIgnoreCase) &&
					!string.IsNullOrWhiteSpace(pair.Value));
				if (field.Required && !anyElement)
					errors.Add(new BackendFieldError(field.Name, $"{field.Label} needs at least one entry."));
				continue;
			}

			lookup.TryGetValue(field.Name, out string? value);
			if (string.IsNullOrWhiteSpace(value))
			{
				// An unset field falls back to the provider default; only a required field with
				// no default of its own is an error.
				if (field.Required && string.IsNullOrWhiteSpace(field.Default))
					errors.Add(new BackendFieldError(field.Name, $"{field.Label} is required."));
				continue;
			}

			BackendFieldError? error = CheckValue(field, value.Trim());
			if (error is not null)
				errors.Add(error);
		}

		return errors;
	}

	/// <summary>Checks one value against one field; null when it is acceptable.</summary>
	public static BackendFieldError? CheckValue(BackendConfigField field, string value)
	{
		switch (field.Type)
		{
			case BackendFieldType.Int:
				if (!long.TryParse(value, out long number))
					return new BackendFieldError(field.Name, $"{field.Label} must be a whole number.");
				if (field.Min is { } min && number < min)
					return new BackendFieldError(field.Name, $"{field.Label} must be at least {min}.");
				if (field.Max is { } max && number > max)
					return new BackendFieldError(field.Name, $"{field.Label} must be at most {max}.");
				return null;

			case BackendFieldType.Bool:
				return bool.TryParse(value, out bool _)
					? null
					: new BackendFieldError(field.Name, $"{field.Label} must be true or false.");

			case BackendFieldType.Enum:
				if (field.EnumValues is not { Count: > 0 } allowed ||
				    allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
					return null;
				return new BackendFieldError(field.Name,
					$"{field.Label} '{value}' is unknown (use {string.Join(", ", allowed)}).");

			case BackendFieldType.Url:
				return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
				       (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
					? null
					: new BackendFieldError(field.Name, $"{field.Label} must be an absolute http(s) URL.");

			default:
				return null;
		}
	}

	/// <summary>
	///   Full pre-save check of one role: schema shape first (per-field, so a form can mark the
	///   input), then the provider's own semantic validation of the effective section.
	/// </summary>
	public static IReadOnlyList<BackendFieldError> Validate(
		IBackendProvider provider, BackendRole role, IReadOnlyDictionary<string, string?> effective)
	{
		List<BackendFieldError> errors = [.. ValidateFields(provider.DescribeConfiguration(role), effective)];
		if (errors.Count > 0)
			return errors;

		List<string> failures = [];
		provider.ValidateConfiguration(role, ProviderSettings.FromFlat(effective), failures);
		errors.AddRange(failures.Select(message => new BackendFieldError(null, message)));
		return errors;
	}
}
