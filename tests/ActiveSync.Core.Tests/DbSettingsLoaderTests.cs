using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Tests;

/// <summary>
///   B9 — the build-time settings load must tell "fresh install" (table missing → Debug) apart from
///   a real outage (→ Warning that says database-stored settings are NOT applied). The old catch-all
///   logged both at Debug, silently reverting restart-tier settings (TLS/metrics enable, ports) to
///   their POCO defaults.
/// </summary>
public sealed class DbSettingsLoaderTests
{
	private sealed record Line(LogLevel Level, string Message);

	private sealed class CapturingLogger : ILogger
	{
		public List<Line> Lines { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
			Exception? exception, Func<TState, Exception?, string> formatter) =>
			Lines.Add(new Line(logLevel, formatter(state, exception)));
	}

	[Fact]
	public void MissingTable_IsAFreshInstall_LoggedAtDebug_NotWarning()
	{
		// A connectable but empty SQLite database: no GlobalSettings table exists yet.
		string path = Path.Combine(Path.GetTempPath(), $"as-settings-{Guid.NewGuid():N}.sqlite");
		try
		{
			DatabaseOptions database = new() { Provider = "Sqlite", ConnectionString = $"Data Source={path}" };
			CapturingLogger logger = new();

			Dictionary<string, string?> result = DbSettingsLoader.TryLoad(database, logger);

			Assert.Empty(result);
			Assert.DoesNotContain(logger.Lines, l => l.Level == LogLevel.Warning);
			Assert.Contains(logger.Lines, l => l.Level == LogLevel.Debug);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void RealOutage_IsLoggedAtWarning_SayingSettingsAreNotApplied()
	{
		// A path whose parent directory does not exist → SQLite CANTOPEN, which is NOT a missing
		// table: a genuine "can't reach the store" failure, the outage class the finding is about.
		DatabaseOptions database = new()
		{
			Provider = "Sqlite",
			ConnectionString = $"Data Source=/no/such/dir/{Guid.NewGuid():N}/db.sqlite"
		};
		CapturingLogger logger = new();

		Dictionary<string, string?> result = DbSettingsLoader.TryLoad(database, logger);

		Assert.Empty(result);
		Line warning = Assert.Single(logger.Lines, l => l.Level == LogLevel.Warning);
		Assert.Contains("NOT applied", warning.Message);
	}
}
