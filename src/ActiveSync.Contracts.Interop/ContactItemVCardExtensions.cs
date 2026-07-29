// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;

namespace ActiveSync.Contracts.Interop;

/// <summary>
///   Converts between <see cref="ContactItem" /> and FolkerKinzel.VCards' object model.
/// </summary>
public static class ContactItemVCardExtensions
{
	/// <summary>Parses a contact payload into a vCard object.</summary>
	/// <param name="item">The contact as it crossed the store boundary.</param>
	/// <returns>The first (and normally only) vCard in the payload.</returns>
	/// <exception cref="BackendException">The payload contains no parsable vCard.</exception>
	public static VCard ToVCard(this ContactItem item)
	{
		ArgumentNullException.ThrowIfNull(item);

		try
		{
			IReadOnlyList<VCard> parsed = Vcf.Parse(item.VCard);
			return parsed.Count > 0
				? parsed[0]
				: throw new BackendException("The contact payload contains no vCard.");
		}
		catch (Exception ex) when (ex is not BackendException and not OutOfMemoryException)
		{
			throw new BackendException("The contact payload could not be parsed as vCard.", ex);
		}
	}

	/// <summary>
	///   Serializes a vCard into the contact payload the contract expects.
	/// </summary>
	/// <param name="vCard">The contact to serialize.</param>
	/// <param name="version">
	///   The vCard version to emit. Defaults to 3.0, which is what CardDAV servers and EAS clients
	///   interoperate on most widely, and what the gateway's own contact conversion writes.
	/// </param>
	/// <returns>The contact as the contract carries it.</returns>
	public static ContactItem ToContactItem(this VCard vCard, VCdVersion version = VCdVersion.V3_0)
	{
		ArgumentNullException.ThrowIfNull(vCard);
		return new ContactItem { VCard = Vcf.AsString([vCard], version) };
	}
}
