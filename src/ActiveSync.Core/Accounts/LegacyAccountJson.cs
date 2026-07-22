using System.Text.Json;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   Converts pre-role-model account rows (top-level imap/smtp/calDav/cardDav/sieve JSON
///   sections) to the role-keyed <see cref="AccountOptions" /> shape. Mandatory before the
///   snapshot ever deserializes such a row: System.Text.Json silently ignores unknown
///   members, so an unconverted row would DROP its overrides — a user's credential override
///   silently falling back to pass-through is an authentication hazard.
/// </summary>
public static class LegacyAccountJson
{
	private static readonly string[] LegacySections = ["imap", "smtp", "calDav", "cardDav", "sieve"];

	/// <summary>
	///   Root properties the upgrade understands: the <see cref="AccountOptions" /> members (which
	///   are carried over verbatim) plus the legacy backend sections (which become role overrides).
	///   Anything else is a value that would be dropped, so it is logged rather than lost silently.
	/// </summary>
	private static readonly HashSet<string> KnownRootProperties = new(StringComparer.OrdinalIgnoreCase)
	{
		"password", "mailAddress", "admin", "enabled", "autoProvisioned", "oidcSubject", "backends",
		"imap", "smtp", "calDav", "cardDav", "sieve",
	};

	/// <summary>
	///   The converted JSON, or null when the row is already role-keyed (or not an object).
	///   Structural problems land in <paramref name="error" /> instead of an exception.
	/// </summary>
	public static string? TryConvert(string json, out string? error, ILogger? logger = null)
	{
		error = null;
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json);
		}
		catch (JsonException ex)
		{
			error = $"stored JSON does not parse: {ex.Message}";
			return null;
		}

		using (document)
		{
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object ||
			    !LegacySections.Any(section => TryGetCaseInsensitive(root, section, out _)))
				return null;

			// B13: deserialize the root into AccountOptions FIRST so every settable member
			// (Password, MailAddress, Admin, Enabled, AutoProvisioned, OidcSubject) carries over —
			// System.Text.Json ignores the unknown legacy sections. The old whitelist reconstructed
			// only three of them, so a disabled row came back enabled after the in-place upgrade.
			AccountOptions converted;
			try
			{
				converted = JsonSerializer.Deserialize<AccountOptions>(json, AccountStore.JsonOptions)
					?? new AccountOptions();
			}
			catch (Exception ex) when (ex is JsonException or NotSupportedException)
			{
				error = $"stored JSON does not map to an account: {ex.Message}";
				return null;
			}

			List<string> unrecognized = root.EnumerateObject()
				.Select(property => property.Name)
				.Where(name => !KnownRootProperties.Contains(name))
				.ToList();
			if (unrecognized.Count > 0)
				logger?.LogWarning(
					"Legacy account row carried unrecognized root propert{Plural} that will be dropped on upgrade: {Names}",
					unrecognized.Count == 1 ? "y" : "ies", string.Join(", ", unrecognized));

			converted.Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase);

			if (TryGetCaseInsensitive(root, "imap", out JsonElement imap) &&
			    ConvertSection(imap, null) is { } mailStore)
				converted.Backends["MailStore"] = mailStore;
			if (TryGetCaseInsensitive(root, "smtp", out JsonElement smtp) &&
			    ConvertSection(smtp, null) is { } mailSubmit)
				converted.Backends["MailSubmit"] = mailSubmit;
			if (TryGetCaseInsensitive(root, "calDav", out JsonElement calDav))
			{
				// A section carrying its own BaseUrl brought its own DAV server under the old
				// rules — that maps to an explicit provider switch. The Tasks role gets the
				// same override (per-role merges no longer flow Calendar → Tasks per user).
				if (ConvertSection(calDav, "caldav") is { } calendar)
				{
					converted.Backends["Calendar"] = calendar;
					converted.Backends["Tasks"] = ConvertSection(calDav, null)!;
				}
			}

			if (TryGetCaseInsensitive(root, "cardDav", out JsonElement cardDav) &&
			    ConvertSection(cardDav, "carddav") is { } contacts)
				converted.Backends["Contacts"] = contacts;
			if (TryGetCaseInsensitive(root, "sieve", out JsonElement sieve) &&
			    ConvertSection(sieve, null) is { } oof)
			{
				// enabled:true was the per-user opt-in; the new equivalent is naming the
				// provider (which then requires an explicit Host — the "defaults to the
				// IMAP host" convenience is gone).
				if (TryGetCaseInsensitive(sieve, "enabled", out JsonElement sieveEnabled) &&
				    sieveEnabled.ValueKind == JsonValueKind.True)
					oof.Provider = "sieve";
				converted.Backends["Oof"] = oof;
			}

			if (converted.Backends.Count == 0)
				converted.Backends = null;
			return JsonSerializer.Serialize(converted, AccountStore.JsonOptions);
		}
	}

	/// <summary>
	///   One legacy backend section → a role override: userName/password/enabled become the
	///   host-reserved fields, everything else becomes flat provider settings (arrays as
	///   indexed keys). <paramref name="providerWhenSelfContained" /> is set when the section
	///   carries its own BaseUrl (an explicit provider switch under the new rules).
	/// </summary>
	private static BackendRoleOverride? ConvertSection(JsonElement section, string? providerWhenSelfContained)
	{
		if (section.ValueKind != JsonValueKind.Object)
			return null;
		BackendRoleOverride converted = new();
		Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase);
		foreach (JsonProperty property in section.EnumerateObject())
			switch (property.Name.ToLowerInvariant())
			{
				case "username":
					converted.UserName = property.Value.GetString();
					break;
				case "password":
					converted.Password = property.Value.GetString();
					break;
				case "enabled":
					if (property.Value.ValueKind == JsonValueKind.False)
						converted.Enabled = false;
					break;
				case "baseurl":
					settings[property.Name] = property.Value.GetString();
					converted.Provider ??= providerWhenSelfContained;
					break;
				default:
					if (property.Value.ValueKind == JsonValueKind.Array)
					{
						int index = 0;
						foreach (JsonElement item in property.Value.EnumerateArray())
							settings[$"{property.Name}:{index++}"] = Scalar(item);
					}
					else if (Scalar(property.Value) is { } scalar)
						settings[property.Name] = scalar;

					break;
			}

		if (converted.Enabled == false)
			return new BackendRoleOverride { Enabled = false };
		if (settings.Count > 0)
			converted.Settings = settings;
		return converted is { Enabled: null, Provider: null, UserName: null, Password: null, Settings: null }
			? null
			: converted;
	}

	private static string? Scalar(JsonElement value)
	{
		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number => value.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => null
		};
	}

	private static bool TryGetCaseInsensitive(JsonElement element, string name, out JsonElement value)
	{
		foreach (JsonProperty property in element.EnumerateObject())
			if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				value = property.Value;
				return true;
			}

		value = default;
		return false;
	}
}
