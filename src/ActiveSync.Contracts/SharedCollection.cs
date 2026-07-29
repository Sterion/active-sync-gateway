// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   A shared CalDAV collection reference: an href plus whether the grant is read-only (enforced
///   gateway-side, on top of whatever the DAV server itself allows). The host resolves these from
///   config or from a database grant (`eas share`) and hands them to the calendar provider on
///   <see cref="BackendConnectionContext.SharedCollections" />.
/// </summary>
/// <remarks>
///   The "href|ro" CONFIG STRING and its parsing/validation are deliberately NOT here: a delimited
///   composite string is a wire encoding, and parsing one inside the plugin contract invites a
///   plugin to invent more of them. The record is the typed value; the entry syntax belongs to the
///   provider that reads the setting.
/// </remarks>
public sealed record SharedCollection
{
	/// <summary>The collection's href — an absolute path or a same-host URL, kept verbatim.</summary>
	public required string Href { get; init; }

	/// <summary>Whether the grant is read-only (client writes are silently reverted).</summary>
	public bool ReadOnly { get; init; }
}
