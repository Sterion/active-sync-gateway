// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   A shared CalDAV collection reference resolved from config ("/path/" or same-host URL,
///   optional "|ro" suffix) or from a database grant (`eas share`). ReadOnly is enforced
///   gateway-side, on top of whatever the DAV server itself allows.
///   <see cref="Validate" /> is stricter than <see cref="Parse" /> about a trailing "|xxx"
///   segment — Parse treats anything that is not exactly "ro"/"rw" as part of the
///   href rather than guessing at a mode, but Validate still REJECTS an entry whose trailing
///   segment looks like an attempted (but misspelled) mode suffix, so a typo is reported rather
///   than silently absorbed into the href.
/// </summary>
public sealed record SharedCollection(string Href, bool ReadOnly)
{
	/// <summary>Parses "href" / "href|ro"; the href part is kept verbatim.</summary>
	public static SharedCollection Parse(string entry)
	{
		int separator = entry.LastIndexOf('|');
		if (separator < 0)
			return new SharedCollection(entry.Trim(), false);
		string mode = entry[(separator + 1)..].Trim();
		// DAV hrefs may themselves contain '|', so the trailing segment after the LAST '|' is
		// treated as a mode delimiter ONLY when it is exactly "ro"/"rw" — the two suffixes this
		// format recognizes. Anything else is part of the href, not a suffix: reinterpreting it as
		// one would both truncate the href AND silently flip the access decision, which is worse
		// than treating the whole string as a (default read-write, like any unsuffixed href) href.
		if (!mode.Equals("ro", StringComparison.OrdinalIgnoreCase) &&
		    !mode.Equals("rw", StringComparison.OrdinalIgnoreCase))
			return new SharedCollection(entry.Trim(), false);
		// Fail CLOSED between the two recognized suffixes — only an explicit "|rw" grants
		// read-write; "|ro" is read-only. (An unrecognized suffix no longer reaches this line — the
		// branch above keeps it as part of the href instead of guessing at a mode for it.)
		bool readOnly = !mode.Equals("rw", StringComparison.OrdinalIgnoreCase);
		return new SharedCollection(entry[..separator].Trim(), readOnly);
	}

	/// <summary>Returns a failure message for an unusable entry, null when valid.</summary>
	public static string? Validate(string entry, string baseUrl)
	{
		SharedCollection parsed = Parse(entry);
		int separator = entry.LastIndexOf('|');
		if (separator >= 0)
		{
			string mode = entry[(separator + 1)..].Trim();
			if (!mode.Equals("ro", StringComparison.OrdinalIgnoreCase) &&
			    !mode.Equals("rw", StringComparison.OrdinalIgnoreCase))
				return $"'{entry}' has an unknown mode suffix '{mode}' (use \"|ro\", \"|rw\", or nothing).";
		}

		if (parsed.Href.StartsWith('/'))
			return null;
		if (!Uri.TryCreate(parsed.Href, UriKind.Absolute, out Uri? uri) ||
		    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			return $"'{entry}' must be an absolute path (\"/cal/team/\") or an http(s) URL.";
		// An absolute URL must be proven same-host. If the BaseUrl cannot be parsed there is
		// no host to compare against, so fail CLOSED — otherwise the old `TryCreate(baseUrl) && ...`
		// short-circuited to "valid" and an absolute href to an attacker host was admitted.
		if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
			return $"'{entry}' is an absolute URL, but the DAV BaseUrl '{baseUrl}' could not be parsed " +
			       "to verify it targets the same host; use an absolute path (\"/cal/team/\") instead.";
		if (!string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
			return $"'{entry}' points at host '{uri.Host}', but the DAV BaseUrl host is '{baseUri.Host}'.";
		return null;
	}
}
