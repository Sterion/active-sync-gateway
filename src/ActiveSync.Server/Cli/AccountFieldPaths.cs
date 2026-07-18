using System.Reflection;
using ActiveSync.Core.Options;

namespace ActiveSync.Server.Cli;

/// <summary>
///   Config-path access to <see cref="AccountOptions" /> fields ("MailAddress",
///   "Imap:Host", "CalDav:Enabled", ...), reflected once from the options classes so the
///   CLI's valid-key list can never drift from what config binding accepts.
/// </summary>
internal static class AccountFieldPaths
{
	internal sealed record FieldPath(
		string Key,
		Type ValueType,
		bool IsSecret,
		Func<AccountOptions, object?> Get,
		Action<AccountOptions, object?> Set);

	private static readonly Dictionary<string, FieldPath> Paths = Build();

	internal static IReadOnlyCollection<string> Keys { get; } =
		Paths.Values.Select(p => p.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

	/// <summary>The four backend password keys accepted by `user secret`.</summary>
	internal static IReadOnlyCollection<string> BackendSecretKeys { get; } =
		Paths.Values.Where(p => p.IsSecret && p.Key.Contains(':')).Select(p => p.Key).ToArray();

	internal static FieldPath? Find(string key)
	{
		return Paths.GetValueOrDefault(key);
	}

	/// <summary>Parses a CLI string into the field's scalar type; error message on failure.</summary>
	internal static bool TryParseValue(FieldPath path, string raw, out object? value, out string? error)
	{
		Type type = Nullable.GetUnderlyingType(path.ValueType) ?? path.ValueType;
		error = null;
		value = null;
		if (type == typeof(string))
		{
			value = raw;
			return true;
		}

		if (type == typeof(int))
		{
			if (int.TryParse(raw, out int i))
			{
				value = i;
				return true;
			}
		}
		else if (type == typeof(bool))
		{
			if (bool.TryParse(raw, out bool b))
			{
				value = b;
				return true;
			}
		}
		else if (type == typeof(char))
		{
			if (raw.Length == 1)
			{
				value = raw[0];
				return true;
			}
		}

		error = $"'{raw}' is not a valid {type.Name} for {path.Key}.";
		return false;
	}

	private static Dictionary<string, FieldPath> Build()
	{
		Dictionary<string, FieldPath> paths = new(StringComparer.OrdinalIgnoreCase);
		foreach (PropertyInfo property in typeof(AccountOptions).GetProperties())
		{
			Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
			if (type == typeof(string))
			{
				PropertyInfo p = property;
				paths[p.Name] = new FieldPath(p.Name, p.PropertyType, p.Name == "Password",
					account => p.GetValue(account),
					(account, value) => p.SetValue(account, value));
			}
			else if (type.IsClass)
			{
				foreach (PropertyInfo sub in type.GetProperties())
				{
					PropertyInfo section = property;
					PropertyInfo p = sub;
					string key = $"{section.Name}:{p.Name}";
					paths[key] = new FieldPath(key, p.PropertyType, p.Name == "Password",
						account => section.GetValue(account) is { } s ? p.GetValue(s) : null,
						(account, value) =>
						{
							object? s = section.GetValue(account);
							if (s is null)
							{
								if (value is null)
									return;
								s = Activator.CreateInstance(section.PropertyType)!;
								section.SetValue(account, s);
							}

							p.SetValue(s, value);
							// Nulling the last field of a section removes the empty section.
							if (value is null &&
							    section.PropertyType.GetProperties().All(q => q.GetValue(s) is null))
								section.SetValue(account, null);
						});
				}
			}
		}

		return paths;
	}
}
