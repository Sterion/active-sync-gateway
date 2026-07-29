// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using MimeKit;

namespace ActiveSync.Contracts.Interop;

/// <summary>
///   Converts between <see cref="MailItem" /> and MimeKit's object model, for a plugin whose
///   backend speaks MIME rather than raw bytes.
/// </summary>
/// <remarks>
///   The contract carries RFC822 BYTES and never <see cref="MimeMessage" /> itself: a parsed
///   object model in a contract signature would force MimeKit onto every plugin (a notes plugin
///   included) and pin each of them to the host's exact MailKit version, because the loader would
///   then have to share that assembly. Keeping the conversion here — in an optional package a
///   plugin ships in its own folder — is what leaves plugins free to pin their own MimeKit.
/// </remarks>
public static class MailItemMimeExtensions
{
	/// <summary>Parses a message's RFC822 bytes into MimeKit's object model.</summary>
	/// <param name="item">The message as it crossed the store boundary.</param>
	/// <returns>The parsed message.</returns>
	/// <exception cref="BackendException">The bytes are not a parsable RFC822 message.</exception>
	public static MimeMessage ToMimeMessage(this MailItem item)
	{
		ArgumentNullException.ThrowIfNull(item);

		try
		{
			using MemoryStream stream = new(item.Rfc822.ToArray(), false);
			return MimeMessage.Load(stream);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// Same posture as IcalHelpers.Load: a malformed payload surfaces as the contract's own
			// backend error rather than as a raw MimeKit exception the caller cannot classify.
			throw new BackendException("The message could not be parsed as RFC822.", ex);
		}
	}

	/// <summary>
	///   Serializes a MimeKit message into the <see cref="MailItem" /> the contract expects,
	///   attaching the flags and metadata that live OUTSIDE the message.
	/// </summary>
	/// <param name="message">The message to serialize.</param>
	/// <param name="flags">The message's sync-relevant flags.</param>
	/// <param name="received">
	///   The backend's own delivery timestamp, when it has one; null otherwise.
	/// </param>
	/// <param name="categories">
	///   The message's user categories, or null for none. Copied, so the caller may reuse its list.
	/// </param>
	/// <returns>The message as the contract carries it.</returns>
	public static MailItem ToMailItem(
		this MimeMessage message,
		MailFlags flags,
		DateTimeOffset? received = null,
		IReadOnlyList<string>? categories = null)
	{
		ArgumentNullException.ThrowIfNull(message);
		ArgumentNullException.ThrowIfNull(flags);

		// CRLF explicitly rather than whatever the platform default resolves to: RFC 5322 line
		// endings are CRLF, and these bytes are what every other backend and the host itself will
		// parse. (IcalHelpers.Serialize normalizes for the same reason.)
		FormatOptions options = FormatOptions.Default.Clone();
		options.NewLineFormat = NewLineFormat.Dos;

		using MemoryStream buffer = new();
		message.WriteTo(options, buffer);

		return new MailItem
		{
			// ToArray() gives the dedicated, never-reused buffer the contract's ownership rule
			// requires — the host may cache these bytes indefinitely.
			Rfc822 = buffer.ToArray(),
			Flags = flags,
			Received = received,
			Categories = categories is null ? [] : [.. categories]
		};
	}
}
