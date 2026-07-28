// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Xml.Linq;
using ActiveSync.Protocol;

namespace ActiveSync.Contracts;

/// <summary>A username/password pair as presented to (or resolved for) a backend connection.</summary>
/// <param name="UserName">The backend-facing username. Never assumed to be an identity — a per-backend user name can be renamed freely.</param>
/// <param name="Password">The backend-facing password, in plaintext. Masked as "***" in the record's generated <c>ToString()</c> — never logged in the clear.</param>
public sealed record BackendCredentials(string UserName, string Password)
{
	// The compiler-synthesized record ToString() would print Password in plaintext — and this
	// type is published plugin contract that lands in logs, exception messages and debugger views,
	// both directly and nested inside ResolvedRole / BackendConnectionContext (whose own ToString()
	// calls this one). Mask the secret by overriding PrintMembers so the record shape is preserved
	// and every enclosing record inherits the redaction.
	//
	// The mask token is a local literal on purpose: Contracts depends only on Protocol + the
	// Microsoft.Extensions abstractions and CANNOT reference ActiveSync.Core.Administration
	// .SecretRedaction across the dependency boundary. It is kept identical to SecretRedaction.Mask
	// ("***") by convention.
	private bool PrintMembers(System.Text.StringBuilder builder)
	{
		builder.Append("UserName = ").Append(UserName).Append(", Password = ***");
		return true;
	}
}

/// <summary>A folder/collection as reported by a backend store.</summary>
public sealed record BackendFolder(
	string BackendKey, // stable backend identifier, e.g. "imap:INBOX/Sub" or "caldav:/user/cal1/"
	string DisplayName,
	string? ParentBackendKey,
	int EasType, // EasFolderType value
	string EasClass); // EasClass value

/// <summary>Client body preference from Sync/ItemOperations options (AirSyncBase BodyPreference).</summary>
/// <summary>
///   The client's body preference plus the negotiated-protocol flag converters need:
///   <see cref="Eas16" /> selects the 16.x shapes (airsyncbase:Location instead of
///   calendar:Location, draft/attachment metadata) without threading a version type
///   through every store signature.
/// </summary>
public sealed record BodyPreference(int Type, long? TruncationSize, bool AllOrNone, bool Eas16 = false)
{
	/// <summary>Convenience default: plain text (Type 1), truncated at 32 KB, AllOrNone false, pre-16.x shapes.</summary>
	public static readonly BodyPreference PlainText = new(1, 32 * 1024, false);
}

/// <summary>Server-side filter for a collection (from AirSync FilterType).</summary>
public sealed record ContentFilter(DateTime? SinceUtc)
{
	/// <summary>No date filtering — every item matches. Used for classes that are never date-filtered (contacts, tasks, notes) and as the fallback for an unrecognized FilterType.</summary>
	public static readonly ContentFilter All = new((DateTime?)null);

	/// <summary>Maps an AirSync FilterType value for the Email class to a date window (1 = 1 day back … 7 = 6 months back); any other value returns <see cref="All" />.</summary>
	/// <param name="filterType">The client-supplied AirSync FilterType.</param>
	/// <returns>A filter matching items no older than the mapped window, or <see cref="All" /> for an unrecognized value.</returns>
	public static ContentFilter FromMailFilterType(int filterType)
	{
		return filterType switch
		{
			1 => new ContentFilter(DateTime.UtcNow.AddDays(-1)),
			2 => new ContentFilter(DateTime.UtcNow.AddDays(-3)),
			3 => new ContentFilter(DateTime.UtcNow.AddDays(-7)),
			4 => new ContentFilter(DateTime.UtcNow.AddDays(-14)),
			5 => new ContentFilter(DateTime.UtcNow.AddMonths(-1)),
			6 => new ContentFilter(DateTime.UtcNow.AddMonths(-3)),
			7 => new ContentFilter(DateTime.UtcNow.AddMonths(-6)),
			_ => All
		};
	}

	/// <summary>Maps an AirSync FilterType value for the Calendar class to a date window (4 = 2 weeks back … 7 = 6 months back); any other value (including the mail-only 1-3) returns <see cref="All" />.</summary>
	/// <param name="filterType">The client-supplied AirSync FilterType.</param>
	/// <returns>A filter matching items no older than the mapped window, or <see cref="All" /> for an unrecognized value.</returns>
	public static ContentFilter FromCalendarFilterType(int filterType)
	{
		return filterType switch
		{
			4 => new ContentFilter(DateTime.UtcNow.AddDays(-14)),
			5 => new ContentFilter(DateTime.UtcNow.AddMonths(-1)),
			6 => new ContentFilter(DateTime.UtcNow.AddMonths(-3)),
			7 => new ContentFilter(DateTime.UtcNow.AddMonths(-6)),
			_ => All
		};
	}

	/// <summary>
	///   Picks the filter window appropriate to a store's content class: mail and calendar
	///   have their own FilterType→date-window mappings; everything else (contacts, tasks,
	///   notes) is never date-filtered.
	/// </summary>
	public static ContentFilter ForClass(string easClass, int filterType)
	{
		return easClass switch
		{
			EasClass.Email => FromMailFilterType(filterType),
			EasClass.Calendar => FromCalendarFilterType(filterType),
			_ => All
		};
	}
}

/// <summary>Content of a fetched item, as EAS ApplicationData child elements.</summary>
public sealed record BackendItem(IReadOnlyList<XElement> ApplicationData);

/// <summary>An attachment payload fetched from a backend.</summary>
public sealed record BackendAttachment(string ContentType, byte[] Content);

/// <summary>
///   Thrown by a backend store or operation for any failure the host should treat as a backend
///   error (as opposed to a bug). NOT sealed — a plugin backend must be able to introduce its own
///   typed subclass that the host's codebase-wide <c>catch (BackendException)</c> idiom still funnels.
/// </summary>
public class BackendException : Exception
{
	/// <summary>Creates the exception with a message only.</summary>
	/// <param name="message">Human-readable description of the failure.</param>
	public BackendException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception wrapping an inner cause.</summary>
	/// <param name="message">Human-readable description of the failure.</param>
	/// <param name="inner">The underlying exception that caused this failure.</param>
	public BackendException(string message, Exception inner) : base(message, inner)
	{
	}
}

/// <summary>
///   Thrown when the referenced backend object no longer exists.
///   Derives from <see cref="BackendException" /> so the codebase-wide
///   `catch (BackendException)` idiom catches it — before, it derived straight from
///   <see cref="Exception" /> and item-gone errors slipped past every backend-error handler.
/// </summary>
public sealed class BackendItemNotFoundException(string message) : BackendException(message);
