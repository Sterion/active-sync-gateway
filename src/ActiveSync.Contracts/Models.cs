// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Xml.Linq;

namespace ActiveSync.Contracts;

/// <summary>A username/password pair as presented to (or resolved for) a backend connection.</summary>
public sealed record BackendCredentials
{
	/// <summary>The backend-facing username. Never assumed to be an identity — a per-backend user name can be renamed freely.</summary>
	public required string UserName { get; init; }

	/// <summary>The backend-facing password, in plaintext. Masked as "***" in the record's <c>ToString()</c> — never logged in the clear.</summary>
	public required string Password { get; init; }

	// The compiler-synthesized record ToString() would print Password in plaintext — and this
	// type is published plugin contract that lands in logs, exception messages and debugger views,
	// both directly and nested inside ResolvedRole / BackendConnectionContext (whose own ToString()
	// calls this one). Mask the secret by overriding PrintMembers so the record shape is preserved
	// and every enclosing record inherits the redaction.
	//
	// The mask token is a local literal on purpose: Contracts depends only on the
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
public sealed record BackendFolder
{
	/// <summary>Stable backend identifier, e.g. "imap:INBOX/Sub" or "caldav:/user/cal1/".</summary>
	public required string BackendKey { get; init; }

	/// <summary>The folder's name as shown to the client.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The parent folder's backend key, or <c>null</c> for a root-level folder.</summary>
	public string? ParentBackendKey { get; init; }

	/// <summary>What kind of folder this is (Inbox, Calendar, a user-created mail folder, …).</summary>
	public required FolderType Type { get; init; }

	/// <summary>EAS content class value served by the owning store ("Email", "Calendar", …).</summary>
	public required string EasClass { get; init; }
}

/// <summary>
///   The client's body preference plus the negotiated-protocol flag converters need:
///   <see cref="Eas16" /> selects the 16.x shapes (airsyncbase:Location instead of
///   calendar:Location, draft/attachment metadata) without threading a version type
///   through every store signature.
/// </summary>
public sealed record BodyPreference
{
	/// <summary>The body shape the client asked for.</summary>
	public required BodyType Type { get; init; }

	/// <summary>Truncate the body at this many bytes; <c>null</c> means "no truncation".</summary>
	public long? TruncationSize { get; init; }

	/// <summary>Whether the client asked for the whole body or none of it (AirSyncBase AllOrNone).</summary>
	public bool AllOrNone { get; init; }

	/// <summary>Whether the negotiated protocol is 16.x, selecting the 16.x element shapes.</summary>
	public bool Eas16 { get; init; }

	/// <summary>Convenience default: plain text, truncated at 32 KB, AllOrNone false, pre-16.x shapes.</summary>
	public static readonly BodyPreference PlainText =
		new() { Type = BodyType.PlainText, TruncationSize = 32 * 1024 };
}

/// <summary>
///   Server-side filter for a collection: items older than <see cref="Since" /> need not be
///   reported. The mapping from the client's wire FilterType to a date window is HOST-side —
///   a store only ever sees the resulting instant.
/// </summary>
public sealed record ContentFilter
{
	/// <summary>Items older than this instant may be omitted; <c>null</c> means no date filtering.</summary>
	public DateTimeOffset? Since { get; init; }

	/// <summary>No date filtering — every item matches.</summary>
	public static readonly ContentFilter All = new();
}

/// <summary>Content of a fetched item, as EAS ApplicationData child elements.</summary>
public sealed record BackendItem
{
	/// <summary>The item's EAS ApplicationData child elements.</summary>
	public required IReadOnlyList<XElement> ApplicationData { get; init; }
}

/// <summary>An attachment payload fetched from a backend.</summary>
public sealed record BackendAttachment
{
	/// <summary>The attachment's MIME content type.</summary>
	public required string ContentType { get; init; }

	/// <summary>The attachment's bytes.</summary>
	public required byte[] Content { get; init; }
}

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
