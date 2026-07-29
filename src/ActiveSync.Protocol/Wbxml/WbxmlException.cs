// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace ActiveSync.Protocol.Wbxml;

/// <summary>
///   Signals that a WBXML document could not be decoded or encoded — malformed input from the
///   wire (truncated data, an unknown code page/token, a bad OPAQUE/base64 run) or a document
///   that cannot be represented (an undefined tag, excessive nesting). Callers map this to an
///   HTTP 400 rather than letting a raw <see cref="FormatException" /> or similar surface as a 500.
/// </summary>
public sealed class WbxmlException : Exception
{
	/// <summary>Creates the exception with a message describing the malformed/undecodable condition.</summary>
	public WbxmlException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception wrapping the lower-level error that caused the WBXML decode/encode to fail.</summary>
	public WbxmlException(string message, Exception inner) : base(message, inner)
	{
	}
}
