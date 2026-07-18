using Npgsql;

namespace ActiveSync.Core.Options;

/// <summary>
///   Accepts libpq-style PostgreSQL URIs (<c>postgresql://user:pass@host:port/db?sslmode=…</c>)
///   as <c>Database:ConnectionString</c> values and converts them to the keyword form Npgsql
///   expects. CloudNativePG app secrets expose exactly this shape (the <c>uri</c> /
///   <c>fqdn-uri</c> keys — and, minus the <c>jdbc:</c> prefix which is stripped here, the
///   <c>jdbc-uri</c> / <c>fqdn-jdbc-uri</c> keys too), so an operator can mount a secret value
///   directly. A URI connection string also implies <c>Provider=Postgres</c>.
/// </summary>
public static class PostgresConnectionUri
{
	// libpq query-parameter names → Npgsql connection-string keywords. Anything not listed
	// is tried verbatim against NpgsqlConnectionStringBuilder, so native Npgsql keywords
	// (e.g. "Pooling") work in the query string too.
	private static readonly Dictionary<string, string> LibpqQueryKeywords = new(StringComparer.OrdinalIgnoreCase)
	{
		["user"] = "Username",
		["password"] = "Password",
		["dbname"] = "Database",
		["host"] = "Host",
		["port"] = "Port",
		["sslmode"] = "SSL Mode",
		["application_name"] = "Application Name",
		["connect_timeout"] = "Timeout"
	};

	public static bool IsPostgresUri(string? connectionString)
	{
		string? stripped = StripJdbcPrefix(connectionString);
		return stripped is not null &&
		       (stripped.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
		        stripped.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>A postgres:// connection string implies Postgres regardless of Provider.</summary>
	public static string EffectiveProvider(DatabaseOptions database)
	{
		return IsPostgresUri(database.ConnectionString) ? "Postgres" : database.Provider;
	}

	/// <summary>
	///   Converts a PostgreSQL URI to an Npgsql keyword connection string. Error messages never
	///   echo the URI itself — it may contain a password.
	/// </summary>
	public static bool TryConvert(string uri, out string connectionString, out string? error)
	{
		connectionString = "";
		if (!Uri.TryCreate(StripJdbcPrefix(uri), UriKind.Absolute, out Uri? parsed) ||
		    string.IsNullOrEmpty(parsed.Host))
		{
			error = "ActiveSync:Database:ConnectionString looks like a PostgreSQL URI but could not " +
			        "be parsed — expected postgresql://user:password@host:port/database.";
			return false;
		}

		string database = Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'));
		if (string.IsNullOrEmpty(database) || database.Contains('/'))
		{
			error = "ActiveSync:Database:ConnectionString PostgreSQL URI must include exactly one " +
			        "database name in its path (postgresql://…host:port/database).";
			return false;
		}

		try
		{
			NpgsqlConnectionStringBuilder builder = new() { Host = parsed.Host, Database = database };
			if (parsed.Port > 0)
				builder.Port = parsed.Port;

			string[] userInfo = parsed.UserInfo.Split(':', 2);
			if (userInfo[0].Length > 0)
				builder.Username = Uri.UnescapeDataString(userInfo[0]);
			if (userInfo.Length == 2)
				builder.Password = Uri.UnescapeDataString(userInfo[1]);

			// libpq treats query parameters as regular connection keywords (CNPG's jdbc-shaped
			// URIs put user/password here); '+' stays literal per libpq percent-encoding rules.
			foreach (string pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = pair.Split('=', 2);
				string key = Uri.UnescapeDataString(parts[0]);
				string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
				try
				{
					builder[LibpqQueryKeywords.GetValueOrDefault(key, key)] = value;
				}
				catch (Exception)
				{
					error = "ActiveSync:Database:ConnectionString PostgreSQL URI has an unsupported " +
					        $"or invalid query parameter '{key}'.";
					return false;
				}
			}

			connectionString = builder.ConnectionString;
			error = null;
			return true;
		}
		catch (Exception ex)
		{
			error = "ActiveSync:Database:ConnectionString PostgreSQL URI could not be converted " +
			        $"to an Npgsql connection string: {ex.GetType().Name}.";
			return false;
		}
	}

	// CNPG's jdbc-uri/fqdn-jdbc-uri keys are the same URI behind a "jdbc:" prefix.
	private static string? StripJdbcPrefix(string? value)
	{
		return value is not null && value.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)
			? value[5..]
			: value;
	}
}
