using System.Security.Cryptography.X509Certificates;

namespace ActiveSync.Core.Options;

/// <summary>Shared checks for provider ValidateConfiguration implementations.</summary>
public static class BackendSettingsValidation
{
	public static void Port(int port, string context, IList<string> failures)
	{
		if (port is < 1 or > 65535)
			failures.Add($"{context}: Port {port} is out of range (1-65535).");
	}

	public static void RequiredHost(string? host, string context, IList<string> failures)
	{
		if (string.IsNullOrWhiteSpace(host))
			failures.Add($"{context}: Host is required.");
	}

	public static void AbsoluteHttpUrl(string? url, string context, IList<string> failures)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
		    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			failures.Add($"{context}: BaseUrl '{url}' must be an absolute http(s) URL.");
	}

	public static void Choice(string? value, string key, string context, IList<string> failures,
		params string[] allowed)
	{
		if (value is not null && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
			failures.Add($"{context}: {key} '{value}' is unknown (use {string.Join(", ", allowed)}).");
	}

	public static void CaPath(string? path, string context, IList<string> failures)
	{
		if (string.IsNullOrWhiteSpace(path))
			return;
		if (!File.Exists(path))
		{
			failures.Add($"{context}: CaCertificatePath '{path}' does not exist.");
			return;
		}

		try
		{
			X509Certificate2Collection collection = new();
			collection.ImportFromPemFile(path);
			if (collection.Count == 0)
				failures.Add($"{context}: CaCertificatePath '{path}' contains no certificates.");
		}
		catch (Exception ex)
		{
			failures.Add($"{context}: CaCertificatePath '{path}' is not a valid PEM certificate file: {ex.Message}");
		}
	}
}
