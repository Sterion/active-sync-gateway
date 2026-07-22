using System.ComponentModel;
using System.Globalization;
using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   `eas logs` — show recent gateway logs persisted to the state database (Information+). Uses the
///   lean bootstrap (DbContext only), so it is fast and works against a stopped gateway. On a
///   shared database it shows every replica (rows carry the machine name).
/// </summary>
internal sealed class LogsCommand(IAnsiConsole terminal) : AsyncCommand<LogsCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption("-s|--since <WINDOW>")]
		[Description("Only entries within this window, e.g. 30m, 2h, 7d (default 1h).")]
		public string Since { get; init; } = "1h";

		[CommandOption("-l|--level <LEVEL>")]
		[Description("Minimum level: Information, Warning, Error or Fatal.")]
		public string? Level { get; init; }

		[CommandOption("-u|--user <USER>")]
		[Description("Only entries tagged with this gateway login.")]
		public string? User { get; init; }

		[CommandOption("-n|--limit <N>")]
		[Description("Maximum entries to show, newest kept (default 100).")]
		public int Limit { get; init; } = 100;
	}

	protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
	{
		if (!TryParseWindow(settings.Since, out TimeSpan window))
		{
			await Console.Error.WriteLineAsync($"Invalid --since '{settings.Since}' (use e.g. 30m, 2h, 7d).");
			return 1;
		}

		string[]? accepted = null;
		if (!string.IsNullOrWhiteSpace(settings.Level))
		{
			accepted = LogQueryService.LevelsAtOrAbove(settings.Level);
			if (accepted.Length == 0)
			{
				await Console.Error.WriteLineAsync(
					$"Invalid --level '{settings.Level}' (use Information, Warning, Error or Fatal).");
				return 1;
			}
		}

		int limit = settings.Limit is > 0 and <= 10_000 ? settings.Limit : 100;
		ServiceProvider? services = await CliServices.TryCreateLeanAsync();
		if (services is null)
			return 1;
		await using ServiceProvider _ = services;
		LogQueryService logs = services.GetRequiredService<LogQueryService>();

		DateTime cutoff = DateTime.UtcNow - window;
		LogQueryService.LogPage page = await logs.QueryAsync(
			new LogQueryService.LogQuery(cutoff, null, accepted, settings.User, null, null, null, limit), ct);
		List<LogEntry> rows = [.. page.Entries];
		rows.Reverse(); // print chronologically (oldest first)

		Table table = new Table().AddColumns("Time (UTC)", "Level", "User", "Source", "Message");
		foreach (LogEntry row in rows)
			table.AddRow(
				new Text(row.TimestampUtc.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
				LevelMarkup(row.Level),
				new Text(row.User ?? "-"),
				new Text(ShortSource(row.SourceContext)),
				new Text(OneLine(row.Message)));
		terminal.Write(table);
		terminal.WriteLine(rows.Count == 0
			? "(no matching log entries)"
			: $"{rows.Count} entr{(rows.Count == 1 ? "y" : "ies")} shown.");
		return 0;
	}

	// Colour the level so errors/warnings pop when the caller's terminal supports it; the markup
	// degrades to the plain level name when colour is off (piped output, or non-colour terminals).
	private static Markup LevelMarkup(string level) => level switch
	{
		"Fatal" => new Markup("[white on red]Fatal[/]"),
		"Error" => new Markup("[red]Error[/]"),
		"Warning" => new Markup("[yellow]Warning[/]"),
		"Information" => new Markup("[green]Information[/]"),
		_ => new Markup($"[grey]{Markup.Escape(level)}[/]")
	};

	private static bool TryParseWindow(string value, out TimeSpan window)
	{
		window = default;
		value = value.Trim();
		if (value.Length < 2)
			return false;
		char unit = char.ToLowerInvariant(value[^1]);
		if (!int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0)
			return false;
		window = unit switch
		{
			'm' => TimeSpan.FromMinutes(n),
			'h' => TimeSpan.FromHours(n),
			'd' => TimeSpan.FromDays(n),
			_ => TimeSpan.Zero
		};
		return window > TimeSpan.Zero;
	}

	private static string ShortSource(string? source) =>
		string.IsNullOrEmpty(source) ? "-" : source.Split('.')[^1];

	private static string OneLine(string message)
	{
		string flat = message.ReplaceLineEndings(" ");
		return flat.Length <= 160 ? flat : flat[..160] + "…";
	}
}
