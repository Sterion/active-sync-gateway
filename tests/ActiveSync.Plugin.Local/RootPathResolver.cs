using System.Text;
using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   Turns the configured <see cref="LocalFilesOptions.RootPath" /> template into one account's
///   absolute store root, and validates the template without touching the filesystem.
/// </summary>
/// <remarks>
///   The substitution is a directory-traversal sink and is treated as one: the gateway's own login
///   validation permits '/', '\' and "..", so a login is sanitized to a conservative character set
///   before it is ever pasted into a path, and the result is then required to stay under
///   <see cref="LocalFilesOptions.BasePath" /> when one is configured. A plugin runs with the
///   gateway's full rights — an escape here would be arbitrary filesystem access.
/// </remarks>
internal static class RootPathResolver
{
	private const string UserPlaceholder = "{user}";
	private const string LocalPartPlaceholder = "{localpart}";

	/// <summary>Resolves one account's absolute store root from the template.</summary>
	/// <param name="options">The provider's bound options.</param>
	/// <param name="login">The gateway login the phone presented.</param>
	/// <returns>The account's absolute, fully-qualified store root.</returns>
	/// <exception cref="BackendException">The template is unusable, or the result escapes <see cref="LocalFilesOptions.BasePath" />.</exception>
	public static string Resolve(LocalFilesOptions options, string login)
	{
		if (string.IsNullOrWhiteSpace(options.RootPath))
			throw new BackendException("local-files: RootPath is not configured.");

		string user = SanitizeSegment(login);
		string localPart = SanitizeSegment(LocalPartOf(login));
		if (user.Length == 0)
			throw new BackendException("local-files: the login contains no character usable in a path.");

		string template = options.RootPath.Trim();
		bool templated = template.Contains(UserPlaceholder, StringComparison.OrdinalIgnoreCase)
		                 || template.Contains(LocalPartPlaceholder, StringComparison.OrdinalIgnoreCase);
		string substituted = template
			.Replace(UserPlaceholder, user, StringComparison.OrdinalIgnoreCase)
			.Replace(LocalPartPlaceholder, localPart, StringComparison.OrdinalIgnoreCase);

		// A template naming no placeholder would hand every account the same tree; append the
		// account instead of failing, so the simplest possible configuration is still per-user.
		string combined = templated ? substituted : Path.Combine(substituted, user);

		string full;
		try
		{
			full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new BackendException($"local-files: RootPath does not resolve to a usable path ({ex.Message}).", ex);
		}

		if (!string.IsNullOrWhiteSpace(options.BasePath))
		{
			string basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.BasePath.Trim()));
			if (!IsUnder(basePath, full))
				throw new BackendException(
					"local-files: the resolved RootPath falls outside the configured BasePath.");
		}

		return full;
	}

	/// <summary>
	///   Validates the option shape. Deliberately does NO filesystem I/O: this runs for every
	///   declared user on every settings-snapshot rebuild, so a stat per call would be an O(users)
	///   syscall storm on a path that may legitimately not exist yet.
	/// </summary>
	/// <param name="options">The bound options to check.</param>
	/// <param name="role">The role being validated, for the message.</param>
	/// <param name="failures">Collector the host renders to the operator.</param>
	public static void ValidateTemplate(LocalFilesOptions options, BackendRole role, IList<string> failures)
	{
		string template = options.RootPath?.Trim() ?? "";
		if (template.Length == 0)
		{
			failures.Add($"local-files ({role}): RootPath is required.");
		}
		else if (template.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
		{
			failures.Add($"local-files ({role}): RootPath contains characters that are not valid in a path.");
		}
		else if (!Path.IsPathRooted(template))
		{
			// A relative path would mean different directories for the gateway and the CLI, which
			// run from different working directories.
			failures.Add($"local-files ({role}): RootPath must be an absolute path.");
		}

		string basePath = options.BasePath?.Trim() ?? "";
		if (basePath.Length > 0 && !Path.IsPathRooted(basePath))
			failures.Add($"local-files ({role}): BasePath must be an absolute path.");

		if (options.PollSeconds is < 1 or > 300)
			failures.Add($"local-files ({role}): PollSeconds must be between 1 and 300.");

		if (options.MaxSearchFileBytes < 1024)
			failures.Add($"local-files ({role}): MaxSearchFileBytes must be at least 1024.");
	}

	/// <summary>The login up to its '@', or the whole login when it carries none.</summary>
	private static string LocalPartOf(string login)
	{
		int at = login.IndexOf('@', StringComparison.Ordinal);
		return at > 0 ? login[..at] : login;
	}

	/// <summary>
	///   Reduces a login to one safe path segment: everything outside [A-Za-z0-9._@+-] becomes '_',
	///   and a segment that is only dots (".", "..") is neutralized.
	/// </summary>
	internal static string SanitizeSegment(string value)
	{
		StringBuilder builder = new(value.Length);
		foreach (char character in value)
			builder.Append(
				char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '@' or '+' or '-'
					? character
					: '_');

		string sanitized = builder.ToString().Trim();
		return sanitized.Trim('.').Length == 0 ? sanitized.Replace('.', '_') : sanitized;
	}

	/// <summary>Whether <paramref name="candidate" /> is <paramref name="root" /> or lives under it.</summary>
	/// <remarks>
	///   Uses <see cref="Path.GetRelativePath" /> rather than a string prefix test so the comparison
	///   follows the platform's own path casing rules.
	/// </remarks>
	private static bool IsUnder(string root, string candidate)
	{
		string relative = Path.GetRelativePath(root, candidate);
		if (relative == ".")
			return true;
		return !Path.IsPathRooted(relative)
		       && !relative.Equals("..", StringComparison.Ordinal)
		       && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
		       && !relative.StartsWith("../", StringComparison.Ordinal);
	}
}
