using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace ActiveSync.Core.Options;

/// <summary>Shared checks for provider ValidateConfiguration implementations.</summary>
public static class BackendSettingsValidation
{
	// B7 (item 37): a CA file is read and PEM-parsed by ValidateConfiguration for EVERY user × role on
	// every snapshot rebuild — with AutoProvisionUsers on that is O(N²) File.Exists + ImportFromPemFile
	// on the auth request thread. Memoize the exists+parse verdict keyed on the file's content stamp
	// (last-write-time + length) so a shared CaCertificatePath is read once until it actually changes.
	// A missing file is never cached — a mount that comes up late must re-check and start passing.
	private static readonly ConcurrentDictionary<string, (long Ticks, long Length, string? Problem)> CaCache =
		new(StringComparer.Ordinal);
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
		string? problem = InspectCaFile(path);
		if (problem is not null)
			failures.Add($"{context}: {problem}");
	}

	/// <summary>
	///   The context-independent verdict for a CA file (null = valid), memoized on its content stamp.
	///   The returned message omits the caller's context prefix so one parse serves every caller.
	/// </summary>
	private static string? InspectCaFile(string path)
	{
		FileInfo info = new(path);
		// Do NOT cache "missing": the file may appear later (a mount coming up) and must re-check.
		if (!info.Exists)
			return $"CaCertificatePath '{path}' does not exist.";

		(long Ticks, long Length) stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
		if (CaCache.TryGetValue(path, out (long Ticks, long Length, string? Problem) cached) &&
		    cached.Ticks == stamp.Ticks && cached.Length == stamp.Length)
			return cached.Problem;

		string? problem = ParseCaFile(path);
		CaCache[path] = (stamp.Ticks, stamp.Length, problem);
		return problem;
	}

	private static string? ParseCaFile(string path)
	{
		try
		{
			X509Certificate2Collection collection = new();
			collection.ImportFromPemFile(path);
			return collection.Count == 0
				? $"CaCertificatePath '{path}' contains no certificates."
				: null;
		}
		catch (Exception ex)
		{
			return $"CaCertificatePath '{path}' is not a valid PEM certificate file: {ex.Message}";
		}
	}
}
