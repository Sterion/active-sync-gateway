// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using ActiveSync.Contracts;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   The "href" / "href|ro" entry syntax of the calendar role's <c>SharedCollections</c> setting.
///   Lives with the provider that READS the setting — the delimited string is a config encoding,
///   not part of the plugin contract, so <see cref="SharedCollection" /> carries only the typed
///   result (<c>Href</c>, <c>ReadOnly</c>).
///   <see cref="Validate" /> is stricter than <see cref="Parse" /> about a trailing "|xxx"
///   segment — Parse treats anything that is not exactly "ro"/"rw" as part of the
///   href rather than guessing at a mode, but Validate still REJECTS an entry whose trailing
///   segment looks like an attempted (but misspelled) mode suffix, so a typo is reported rather
///   than silently absorbed into the href.
/// </summary>
public static class SharedCollectionEntry
{
	/// <summary>Parses "href" / "href|ro"; the href part is kept verbatim.</summary>
	/// <param name="entry">One configured entry.</param>
	/// <returns>The typed grant the entry denotes.</returns>
	public static SharedCollection Parse(string entry)
	{
		int separator = entry.LastIndexOf('|');
		if (separator < 0)
			return new SharedCollection { Href = entry.Trim() };
		string mode = entry[(separator + 1)..].Trim();
		// DAV hrefs may themselves contain '|', so the trailing segment after the LAST '|' is
		// treated as a mode delimiter ONLY when it is exactly "ro"/"rw" — the two suffixes this
		// format recognizes. Anything else is part of the href, not a suffix: reinterpreting it as
		// one would both truncate the href AND silently flip the access decision, which is worse
		// than treating the whole string as a (default read-write, like any unsuffixed href) href.
		if (!mode.Equals("ro", StringComparison.OrdinalIgnoreCase) &&
		    !mode.Equals("rw", StringComparison.OrdinalIgnoreCase))
			return new SharedCollection { Href = entry.Trim() };
		// Fail CLOSED between the two recognized suffixes — only an explicit "|rw" grants
		// read-write; "|ro" is read-only. (An unrecognized suffix no longer reaches this line — the
		// branch above keeps it as part of the href instead of guessing at a mode for it.)
		bool readOnly = !mode.Equals("rw", StringComparison.OrdinalIgnoreCase);
		return new SharedCollection { Href = entry[..separator].Trim(), ReadOnly = readOnly };
	}

	/// <summary>Returns a failure message for an unusable entry, null when valid.</summary>
	/// <param name="entry">One configured entry.</param>
	/// <param name="baseUrl">The DAV role's BaseUrl, used to prove an absolute href is same-host.</param>
	/// <returns>A human-readable failure message, or <c>null</c> when the entry is usable.</returns>
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
