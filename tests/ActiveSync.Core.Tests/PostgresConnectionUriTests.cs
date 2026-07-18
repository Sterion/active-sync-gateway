using ActiveSync.Core.Options;
using Npgsql;

namespace ActiveSync.Core.Tests;

public class PostgresConnectionUriTests
{
	private static NpgsqlConnectionStringBuilder Convert(string uri)
	{
		bool ok = PostgresConnectionUri.TryConvert(uri, out string connectionString, out string? error);
		Assert.True(ok, error);
		return new NpgsqlConnectionStringBuilder(connectionString);
	}

	[Fact]
	public void CnpgUriKey_ConvertsAllComponents()
	{
		// The exact shape of a CloudNativePG app secret's `fqdn-uri` key.
		NpgsqlConnectionStringBuilder result = Convert(
			"postgresql://activesync:s3cret-pw@active-sync-db-rw.active-sync.svc.cluster.local:5432/activesync");
		Assert.Equal("active-sync-db-rw.active-sync.svc.cluster.local", result.Host);
		Assert.Equal(5432, result.Port);
		Assert.Equal("activesync", result.Database);
		Assert.Equal("activesync", result.Username);
		Assert.Equal("s3cret-pw", result.Password);
	}

	[Fact]
	public void CnpgJdbcUriKey_JdbcPrefixAndQueryCredentials_Convert()
	{
		// The exact shape of the `jdbc-uri` key: jdbc: prefix, credentials in the query.
		NpgsqlConnectionStringBuilder result = Convert(
			"jdbc:postgresql://active-sync-db-rw.active-sync:5432/activesync?password=s3cret-pw&user=activesync");
		Assert.Equal("active-sync-db-rw.active-sync", result.Host);
		Assert.Equal("activesync", result.Username);
		Assert.Equal("s3cret-pw", result.Password);
		Assert.Equal("activesync", result.Database);
	}

	[Fact]
	public void PostgresScheme_AndDefaultPort_Work()
	{
		NpgsqlConnectionStringBuilder result = Convert("postgres://u:p@db.example.com/mydb");
		Assert.Equal("db.example.com", result.Host);
		Assert.Equal(5432, result.Port); // Npgsql default — the URI carried no port
		Assert.Equal("mydb", result.Database);
	}

	[Fact]
	public void PercentEncodedCredentials_AreDecoded()
	{
		NpgsqlConnectionStringBuilder result = Convert("postgresql://us%40er:p%40ss%2Fw%3Ard@host:5433/db");
		Assert.Equal("us@er", result.Username);
		Assert.Equal("p@ss/w:rd", result.Password);
		Assert.Equal(5433, result.Port);
	}

	[Fact]
	public void LibpqQueryParameters_MapToNpgsqlKeywords()
	{
		NpgsqlConnectionStringBuilder result = Convert(
			"postgresql://u:p@host:5432/db?sslmode=require&application_name=eas&connect_timeout=7");
		Assert.Equal(SslMode.Require, result.SslMode);
		Assert.Equal("eas", result.ApplicationName);
		Assert.Equal(7, result.Timeout);
	}

	[Fact]
	public void NativeNpgsqlKeyword_InQuery_PassesThrough()
	{
		NpgsqlConnectionStringBuilder result = Convert("postgresql://u:p@host:5432/db?Pooling=false");
		Assert.False(result.Pooling);
	}

	[Fact]
	public void MissingDatabaseName_Fails()
	{
		Assert.False(PostgresConnectionUri.TryConvert("postgresql://u:p@host:5432", out _, out string? error));
		Assert.Contains("database name", error);
	}

	[Fact]
	public void UnknownQueryParameter_FailsAndNamesIt_WithoutLeakingThePassword()
	{
		bool ok = PostgresConnectionUri.TryConvert(
			"postgresql://u:hunter2@host:5432/db?bogus_param=1", out _, out string? error);
		Assert.False(ok);
		Assert.Contains("bogus_param", error);
		Assert.DoesNotContain("hunter2", error);
	}

	[Theory]
	[InlineData("postgresql://u:p@host/db", true)]
	[InlineData("postgres://u:p@host/db", true)]
	[InlineData("jdbc:postgresql://host/db", true)]
	[InlineData("Host=db;Database=activesync;Username=u;Password=p", false)]
	[InlineData("Data Source=activesync.db", false)]
	[InlineData(null, false)]
	public void IsPostgresUri_Classifies(string? connectionString, bool expected)
	{
		Assert.Equal(expected, PostgresConnectionUri.IsPostgresUri(connectionString));
	}

	[Fact]
	public void EffectiveProvider_UriImpliesPostgres_KeywordKeepsDeclared()
	{
		Assert.Equal("Postgres", PostgresConnectionUri.EffectiveProvider(new DatabaseOptions
		{
			Provider = "Sqlite", // left at default — the URI wins
			ConnectionString = "postgresql://u:p@host/db"
		}));
		Assert.Equal("Sqlite", PostgresConnectionUri.EffectiveProvider(new DatabaseOptions
		{
			Provider = "Sqlite",
			ConnectionString = "Data Source=activesync.db"
		}));
	}
}
