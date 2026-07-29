// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   A store's own key for a folder/collection ("imap:INBOX/Sub", "caldav:/user/cal1/"). Opaque
///   to the host: it is dispatched to the owning store and round-tripped, never parsed.
/// </summary>
/// <remarks>
///   A single-value newtype, so the three key kinds cannot be swapped for one another at a call
///   site the way three bare <c>string</c>s could. Deliberately a POSITIONAL record struct — the
///   contract's models are otherwise init-only property records so they can gain a field without
///   breaking callers, but a newtype over one string is defined by never gaining a second field,
///   so that hazard cannot apply to it.
///   <para>
///     <c>default(FolderKey)</c> exists with a <c>null</c> <see cref="Value" /> despite the
///     non-nullable annotation (every struct has a default) — convention over ceremony: the host
///     never manufactures a default key, and a store must never return one.
///   </para>
/// </remarks>
/// <param name="Value">The store-defined key text.</param>
public readonly record struct FolderKey(string Value);

/// <summary>
///   A store's own key for an item within a folder (an IMAP UID, a DAV href, a local row id).
///   Stable for the lifetime of the item within its folder; opaque to the host.
/// </summary>
/// <remarks>See <see cref="FolderKey" /> for why this is a positional record struct.</remarks>
/// <param name="Value">The store-defined key text.</param>
public readonly record struct ItemKey(string Value);

/// <summary>
///   A store's revision stamp for one item (a flags hash, an ETag, a row version). Fully opaque:
///   the sync engine only ever compares two of them for equality — it never parses one, and never
///   writes a value of its own into this space.
/// </summary>
/// <remarks>See <see cref="FolderKey" /> for why this is a positional record struct.</remarks>
/// <param name="Value">The store-defined revision token.</param>
public readonly record struct ItemRevision(string Value);
