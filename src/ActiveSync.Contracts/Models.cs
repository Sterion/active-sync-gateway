// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

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
/// <remarks>
///   No content-class member: a store serves exactly one class (it implements exactly one class
///   alias interface), so every folder it lists is that class — a per-folder class field could
///   only agree with the owning store or be a bug. The host tags folders with the store's class
///   itself.
/// </remarks>
public sealed record BackendFolder
{
	/// <summary>Stable store-defined key, e.g. "imap:INBOX/Sub" or "caldav:/user/cal1/".</summary>
	public required FolderKey Key { get; init; }

	/// <summary>The folder's name as shown to the client.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The parent folder's key, or <c>null</c> for a root-level folder.</summary>
	public FolderKey? ParentKey { get; init; }

	/// <summary>What kind of folder this is (Inbox, Calendar, a user-created mail folder, …).</summary>
	public required FolderType Type { get; init; }
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

/// <summary>An attachment payload fetched from a backend.</summary>
public sealed record BackendAttachment
{
	/// <summary>The attachment's MIME content type.</summary>
	public required string ContentType { get; init; }

	/// <summary>The attachment's bytes (ownership rule: a dedicated, never-mutated buffer).</summary>
	public required ReadOnlyMemory<byte> Content { get; init; }
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

/// <summary>
///   Thrown by a store that CAN check an update's <c>expected</c> revision precondition
///   (DAV If-Match, JMAP ifInState, a local row version) when the item's current revision
///   differs — a typed signal, so the host can distinguish "the item moved underneath the
///   merge" from any other backend error. The host's response: drop its cached payload,
///   re-fetch, re-merge the client's partial data onto the fresh payload, and retry ONCE with
///   the new revision; a second failure surfaces as the ordinary per-item conflict status.
///   A store that cannot check the precondition never throws this — it ignores
///   <c>expected</c> and applies the write, which is conforming.
/// </summary>
public sealed class BackendPreconditionFailedException(string message) : BackendException(message);
