using System.Globalization;
using System.Text;

namespace ActiveSync.Backends.Sieve;

/// <summary>
///   Renders the gateway-owned sieve script for the out-of-office feature: a vacation
///   action, optionally wrapped in a currentdate window (EAS scheduled Oof; date
///   granularity — sieve's currentdate test compares dates, so start/end times are
///   truncated to their UTC calendar days). One reply body for every audience by design.
/// </summary>
public static class SieveVacationScript
{
	/// <summary>The script name the gateway owns on the sieve server.</summary>
	public const string ScriptName = "eas-gateway";

	public static string Build(string message, DateTime? startUtc = null, DateTime? endUtc = null)
	{
		bool scheduled = startUtc is not null && endUtc is not null;
		StringBuilder script = new();
		script.Append("# Managed by the ActiveSync gateway (Settings/Oof). Manual edits are overwritten.\r\n");
		script.Append(scheduled
			? "require [\"vacation\", \"date\", \"relational\"];\r\n\r\n"
			: "require [\"vacation\"];\r\n\r\n");

		string vacation = "vacation :days 1 text:\r\n" + DotStuff(message) + "\r\n.\r\n;\r\n";
		if (scheduled)
		{
			string start = startUtc!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			string end = endUtc!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			script.Append("if allof (currentdate :zone \"+0000\" :value \"ge\" \"date\" \"").Append(start)
				.Append("\",\r\n          currentdate :zone \"+0000\" :value \"le\" \"date\" \"").Append(end)
				.Append("\")\r\n{\r\n").Append(vacation).Append("}\r\n");
		}
		else
		{
			script.Append(vacation);
		}

		return script.ToString();
	}

	/// <summary>
	///   Sieve multiline (text: ... .) framing: normalize line endings to CRLF and double
	///   any leading dot so a message line of "." cannot terminate the block early.
	/// </summary>
	private static string DotStuff(string message)
	{
		string[] lines = message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		return string.Join("\r\n", lines.Select(l => l.StartsWith('.') ? "." + l : l));
	}
}
