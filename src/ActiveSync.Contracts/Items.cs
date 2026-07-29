// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   One mail message as it crosses the store boundary: the raw RFC822 bytes plus the typed
///   metadata that lives OUTSIDE the message (flags, categories, the backend's own delivery
///   timestamp). The bytes are the currency — no parsed object model ever crosses the boundary.
/// </summary>
public sealed record MailItem
{
	/// <summary>
	///   The raw RFC822 message bytes. Ownership rule: the memory must be a dedicated,
	///   never-mutated buffer — the host may cache it indefinitely, so a store that pools
	///   buffers must copy before returning.
	/// </summary>
	public required ReadOnlyMemory<byte> Rfc822 { get; init; }

	/// <summary>The message's sync-relevant flags.</summary>
	public required MailFlags Flags { get; init; }

	/// <summary>
	///   The message's user categories (IMAP custom keywords / JMAP keywords, system keywords
	///   excluded). Legitimately opaque to the host — it round-trips them, never interprets them.
	/// </summary>
	public IReadOnlyList<string> Categories { get; init; } = [];

	/// <summary>
	///   The backend's own delivery timestamp (IMAP INTERNALDATE / JMAP receivedAt), preferred
	///   over the sender-supplied Date header. Null when the backend has none.
	/// </summary>
	public DateTimeOffset? Received { get; init; }
}

/// <summary>A mail message's sync-relevant flags, as stored beside (never inside) the RFC822.</summary>
public sealed record MailFlags
{
	/// <summary>Whether the message has been read (\Seen).</summary>
	public bool Seen { get; init; }

	/// <summary>Whether the message carries the follow-up flag (\Flagged).</summary>
	public bool Flagged { get; init; }

	/// <summary>Whether the message has been answered (\Answered).</summary>
	public bool Answered { get; init; }

	/// <summary>
	///   Whether the message has been forwarded ($Forwarded). NOT optional: the host consumes it
	///   (it feeds EAS LastVerbExecuted) and stores write it after SmartForward — dropping it
	///   would lose data the host consumes.
	/// </summary>
	public bool Forwarded { get; init; }

	/// <summary>Whether the message is a draft (\Draft).</summary>
	public bool Draft { get; init; }
}

/// <summary>
///   A value that may or may not have been supplied. The everyday mail Change carries presence ON
///   THE VALUE: an unset <see cref="Optional{T}" /> means "the client did not send this field",
///   which is distinct from any actual value (including null) and never means "clear it". A
///   parallel field-set was deliberately rejected — it can drift from the values with nothing to
///   catch it.
/// </summary>
/// <typeparam name="T">The carried value's type.</typeparam>
public readonly struct Optional<T>
{
	private readonly T _value;

	private Optional(T value)
	{
		_value = value;
		HasValue = true;
	}

	/// <summary>Whether a value was supplied. <c>default(Optional&lt;T&gt;)</c> is "not supplied".</summary>
	public bool HasValue { get; }

	/// <summary>The supplied value. Throws when <see cref="HasValue" /> is false.</summary>
	public T Value => HasValue
		? _value
		: throw new InvalidOperationException("The optional value was not supplied.");

	/// <summary>Wraps a supplied value, so assigning a plain value marks the field as sent.</summary>
	/// <param name="value">The supplied value.</param>
	public static implicit operator Optional<T>(T value)
	{
		return new Optional<T>(value);
	}

	/// <summary>
	///   Wraps a supplied value explicitly. Needed because C# never applies a user-defined
	///   conversion whose operand type is an interface — so for an interface-typed
	///   <typeparamref name="T" /> (e.g. the categories list) the implicit operator cannot fire
	///   and this named factory is the way to mark the field as sent.
	/// </summary>
	/// <param name="value">The supplied value.</param>
	/// <returns>The supplied-value wrapper.</returns>
	public static Optional<T> Of(T value)
	{
		return new Optional<T>(value);
	}
}

/// <summary>
///   The everyday mail Change (<see cref="IMailStore.UpdateFlagsAsync" />): a flags/categories
///   patch that never rewrites the message. Each field applies only when supplied — the typed
///   equivalent of the presence-guarded element handling EAS partial updates require ("client did
///   not send categories" must stay distinguishable from "client cleared categories").
/// </summary>
public sealed record MailFlagsPatch
{
	/// <summary>The read state to apply, when supplied.</summary>
	public Optional<bool> Read { get; init; }

	/// <summary>The follow-up flag state to apply, when supplied.</summary>
	public Optional<bool> Flagged { get; init; }

	/// <summary>
	///   The complete category set to apply, when supplied (a full replacement of the user
	///   categories; system keywords are never touched). An empty list clears them.
	/// </summary>
	public Optional<IReadOnlyList<string>> Categories { get; init; }
}

/// <summary>
///   Fetch options for the mail store. EMPTY today by design: fetches are always-full, exactly
///   the pre-contract behaviour. Exists so a future truncation hint (e.g. MaxBodyBytes for IMAP
///   BODYSTRUCTURE / JMAP bodyValues stores) can be added additively without touching a signature.
/// </summary>
public sealed record MailFetchOptions
{
	/// <summary>The full fetch — the only shape that exists today.</summary>
	public static readonly MailFetchOptions Full = new();
}

/// <summary>One calendar event as an iCalendar document (a VEVENT with its VTIMEZONE context).</summary>
/// <remarks>
///   A single-property record rather than a bare string on purpose: it is the extension point if
///   the class later needs metadata beside the payload (as mail already does), and it makes the
///   generic store aliases type-distinct.
/// </remarks>
public sealed record CalendarItem
{
	/// <summary>The full iCalendar text of the event (including exceptions and time zones).</summary>
	public required string ICalendar { get; init; }
}

/// <summary>One task as an iCalendar document (a VTODO). See <see cref="CalendarItem" /> remarks.</summary>
public sealed record TaskItem
{
	/// <summary>The full iCalendar text of the task.</summary>
	public required string ICalendar { get; init; }
}

/// <summary>One contact as a vCard document. See <see cref="CalendarItem" /> remarks.</summary>
public sealed record ContactItem
{
	/// <summary>The full vCard text of the contact.</summary>
	public required string VCard { get; init; }
}

/// <summary>
///   One note, fully typed — there is no accepted notes interchange standard, and notes is the
///   simplest possible plugin, so it gets a typed record instead of format ceremony.
/// </summary>
public sealed record NoteItem
{
	/// <summary>The note's subject line.</summary>
	public required string Subject { get; init; }

	/// <summary>The note's body.</summary>
	public required TextBody Body { get; init; }

	/// <summary>The note's categories.</summary>
	public IReadOnlyList<string> Categories { get; init; } = [];

	/// <summary>When the note was last modified; null when the store does not track it.</summary>
	public DateTimeOffset? LastModified { get; init; }
}

/// <summary>A typed text body: its shape and its full content.</summary>
/// <remarks>
///   No Truncated flag, deliberately: a store always hands over the FULL body — truncation is an
///   EAS presentation concern applied host-side per the client's body preference. A truncation
///   marker at this seam could only mean the store lost data it should not have.
/// </remarks>
public sealed record TextBody
{
	/// <summary>The body's shape (plain text or HTML; stores never produce RTF or MIME here).</summary>
	public required BodyType Type { get; init; }

	/// <summary>The full body text.</summary>
	public required string Content { get; init; }
}
