using ActiveSync.Backends.Smtp;
using ActiveSync.Contracts;
using MailKit.Net.Smtp;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D1 (coverage): the RFC 1870 SIZE preflight decision in <see cref="SmtpSubmitBackend" />. The
///   symptom the fix prevents — an oversized message streaming the whole DATA body before a 552
///   reject — needs a real submission MSA advertising a small <c>MaxSize</c>, which the unit
///   environment cannot exhibit; this proves the guard's boundary logic directly (throws only when
///   SIZE is advertised with a positive limit that the message exceeds), which is what spares the
///   DATA transfer and surfaces a distinct non-retryable error. Coverage for the fix, not a
///   reproduction of the streaming behaviour.
/// </summary>
public class SmtpMaxSizePreflightTests
{
	[Fact]
	public void OversizedMessage_WithAdvertisedSizeLimit_ThrowsWithSizeHint()
	{
		BackendException ex = Assert.Throws<BackendException>(() =>
			SmtpSubmitBackend.EnsureWithinMaxSize(2000, SmtpCapabilities.Size, 1000));

		// The size hint distinguishes it from a generic/transient submit failure.
		Assert.Contains("2000", ex.Message);
		Assert.Contains("1000", ex.Message);
	}

	[Fact]
	public void MessageWithinAdvertisedLimit_DoesNotThrow()
	{
		SmtpSubmitBackend.EnsureWithinMaxSize(500, SmtpCapabilities.Size, 1000);
	}

	[Fact]
	public void MessageExactlyAtLimit_DoesNotThrow()
	{
		SmtpSubmitBackend.EnsureWithinMaxSize(1000, SmtpCapabilities.Size, 1000);
	}

	[Fact]
	public void ServerWithoutSizeCapability_DoesNotThrow()
	{
		// No SIZE advertised → MaxSize is meaningless; never preflight-reject.
		SmtpSubmitBackend.EnsureWithinMaxSize(9999, SmtpCapabilities.None, 1000);
	}

	[Fact]
	public void SizeAdvertisedWithoutLimit_DoesNotThrow()
	{
		// SIZE advertised but MaxSize == 0 means "no stated limit" — must not reject.
		SmtpSubmitBackend.EnsureWithinMaxSize(9999, SmtpCapabilities.Size, 0);
	}
}
