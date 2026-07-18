using ActiveSync.Server.Setup;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The ActiveSync:Log Mode/Format knobs: template selection, and that the Standard and
///   Extended templates actually render dates, full level names, the logger category and
///   the trailing properties blob.
/// </summary>
public sealed class LoggingSetupTests
{
	[Theory]
	[InlineData("Simple", "Text")]
	[InlineData("Standard", "Json")]
	[InlineData("Extended", "Json")]
	public void SimpleAndJson_UseNoTemplate(string mode, string format)
	{
		Assert.Null(LoggingSetup.SelectTemplate(mode, format));
	}

	[Fact]
	public void Standard_HasDate_FullLevel_AndCategory()
	{
		string? template = LoggingSetup.SelectTemplate("Standard", "Text");
		Assert.NotNull(template);
		Assert.Contains("yyyy-MM-dd", template);
		Assert.Contains("{Level,-11}", template);
		Assert.Contains("{SourceContext}", template);
		Assert.DoesNotContain("{Properties", template);
	}

	[Fact]
	public void Extended_AddsThePropertiesBlob()
	{
		string? template = LoggingSetup.SelectTemplate("extended", "text"); // case-insensitive
		Assert.NotNull(template);
		Assert.Contains("{Properties:j}", template);
	}

	[Fact]
	public void StandardTemplate_RendersFullLevelNames()
	{
		StringWriter output = new();
		using Logger logger = new LoggerConfiguration()
			.MinimumLevel.Debug()
			.WriteTo.Sink(new TemplateSink(LoggingSetup.StandardTemplate, output))
			.CreateLogger();
		logger.ForContext(Constants.SourceContextPropertyName, "ActiveSync.Server.Eas.PingHandler")
			.Debug("Ping: changes in {Folders} for {User}", "INBOX", "anna@example.com");

		string line = output.ToString();
		Assert.Contains("| Debug       |", line);
		Assert.DoesNotContain("DBG", line);
		Assert.Contains("| ActiveSync.Server.Eas.PingHandler |", line);
		Assert.Contains("Ping: changes in INBOX for anna@example.com", line);
		Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", line);
	}

	[Fact]
	public void ExtendedTemplate_RendersPropertiesNotInTheMessage()
	{
		StringWriter output = new();
		using Logger logger = new LoggerConfiguration()
			.WriteTo.Sink(new TemplateSink(LoggingSetup.ExtendedTemplate, output))
			.CreateLogger();
		logger.ForContext("ConnectionId", "ab12cd")
			.Information("Sent message {MessageId}", "<x@y>");

		string line = output.ToString();
		Assert.Contains("| Information |", line);
		// MessageId is rendered in the message; ConnectionId only exists in the blob.
		Assert.Contains("\"ConnectionId\"", line);
		Assert.Contains("ab12cd", line);
	}

	/// <summary>Renders events through a real output template into a StringWriter.</summary>
	private sealed class TemplateSink(string template, StringWriter output) : ILogEventSink
	{
		private readonly MessageTemplateTextFormatter _formatter = new(template);

		public void Emit(LogEvent logEvent)
		{
			_formatter.Format(logEvent, output);
		}
	}
}
