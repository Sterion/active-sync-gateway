using Serilog;
using Serilog.Formatting.Compact;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Console log shaping from the ActiveSync:Log knobs — Mode (Simple | Standard | Extended)
///   × Format (Text | Json/CLEF). When the operator defines their own sinks under
///   Serilog:WriteTo, the gateway adds NO console sink of its own: their configuration rules
///   (and nothing double-logs). Validation of the knob values happened in
///   ActiveSyncOptionsValidator before any host starts.
/// </summary>
public static class LoggingSetup
{
	/// <summary>Standard: date, full level name (padded to "Information"), logger category.</summary>
	public const string StandardTemplate =
		"{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} | {Level,-11} | {SourceContext} | {Message:lj}{NewLine}{Exception}";

	/// <summary>Extended: Standard plus every structured property the event carries.</summary>
	public const string ExtendedTemplate =
		"{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} | {Level,-11} | {SourceContext} | {Message:lj} | {Properties:j}{NewLine}{Exception}";

	/// <summary>
	///   The output template for a mode/format pair; null when no template applies (Json is
	///   formatter-driven, Simple keeps the stock Serilog console template).
	/// </summary>
	public static string? SelectTemplate(string mode, string format)
	{
		if (format.Equals("Json", StringComparison.OrdinalIgnoreCase))
			return null;
		return mode.ToLowerInvariant() switch
		{
			"standard" => StandardTemplate,
			"extended" => ExtendedTemplate,
			_ => null,
		};
	}

	/// <param name="configuration">The Serilog configuration to add the console sink to.</param>
	/// <param name="appConfiguration">Bound application configuration (knobs + Serilog section).</param>
	/// <param name="alwaysConsole">
	///   True for the CLI banner: it must reach the console even when the operator declared
	///   their own Serilog:WriteTo sinks (which otherwise suppress the built-in one).
	/// </param>
	public static void ConfigureConsole(
		LoggerConfiguration configuration, IConfiguration appConfiguration, bool alwaysConsole = false)
	{
		// Operator-defined sinks win wholesale; ReadFrom.Configuration already added them.
		if (!alwaysConsole && appConfiguration.GetSection("Serilog:WriteTo").GetChildren().Any())
			return;

		string mode = appConfiguration["ActiveSync:Log:Mode"] ?? "Standard";
		string format = appConfiguration["ActiveSync:Log:Format"] ?? "Text";

		// Extended promises "everything about the event" — thread and machine (pod) name
		// included, in both text and CLEF output.
		if (mode.Equals("Extended", StringComparison.OrdinalIgnoreCase))
			configuration.Enrich.WithThreadId().Enrich.WithMachineName();

		if (format.Equals("Json", StringComparison.OrdinalIgnoreCase))
		{
			// CLEF with the rendered message in @m — grep- and LogQL-friendly.
			configuration.WriteTo.Console(new RenderedCompactJsonFormatter());
			return;
		}

		string? template = SelectTemplate(mode, format);
		if (template is null)
			configuration.WriteTo.Console(); // Simple: the stock template, pre-1.0.7 look
		else
			configuration.WriteTo.Console(outputTemplate: template);
	}
}
