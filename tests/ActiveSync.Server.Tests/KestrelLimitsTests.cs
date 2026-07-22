using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace ActiveSync.Server.Tests;

/// <summary>E12 — KeepAliveTimeout bounds idle time BETWEEN requests, never a request in flight.</summary>
public sealed class KestrelLimitsTests
{
	[Fact]
	public void KeepAliveTimeout_ReapsIdleSocketsPromptly_NotAfterAnHour()
	{
		KestrelServerLimits limits = new();

		global::Program.ConfigureKestrelLimits(limits);

		// A 59-minute Ping is safe regardless (Kestrel imposes no per-request cap), so this must be
		// the short between-requests idle reap — not the old 65-minute value that let dead phone
		// sockets linger as zombie connections.
		Assert.True(limits.KeepAliveTimeout <= TimeSpan.FromMinutes(2),
			$"KeepAliveTimeout should reap idle connections within ~2 min, was {limits.KeepAliveTimeout}.");
	}

	[Fact]
	public void RequestBodyAndHeaderLimits_Preserved()
	{
		KestrelServerLimits limits = new();

		global::Program.ConfigureKestrelLimits(limits);

		Assert.Equal(TimeSpan.FromSeconds(60), limits.RequestHeadersTimeout);
		Assert.Equal(64 * 1024 * 1024, limits.MaxRequestBodySize);
	}
}
