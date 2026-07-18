namespace ActiveSync.Server.Eas;

/// <summary>
///   Sanitizes client-supplied strings (usernames, device ids, commands, mail headers)
///   before they are embedded in log output: control characters would let a hostile client
///   forge log lines or smuggle terminal escape sequences into the console.
/// </summary>
public static class LogText
{
	public static string Clean(string? value, int maxLength = 256)
	{
		if (string.IsNullOrEmpty(value))
			return "";
		string text = value.Length > maxLength ? value[..maxLength] : value;
		if (!text.Any(c => char.IsControl(c)))
			return text;
		return string.Create(text.Length, text, static (span, source) =>
		{
			for (int i = 0; i < source.Length; i++)
				span[i] = char.IsControl(source[i]) ? '?' : source[i];
		});
	}
}
