using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Server.Eas;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

public class EndpointAuthTests
{
	private static AuthThrottle Throttle()
	{
		return new AuthThrottle(TestOptionsMonitor.Of(new ActiveSyncOptions()));
	}

	[Fact]
	public async Task BackendOutage_Returns503()
	{
		DefaultHttpContext http = new();
		bool ok = await EndpointAuth.AuthenticateAsync(
			http, new ThrowingSessionFactory(), Throttle(), "1.2.3.4",
			new BackendCredentials("u", "p"), NullLogger.Instance, CancellationToken.None);

		Assert.False(ok);
		Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
	}

	[Fact]
	public async Task RejectedCredentials_Challenge401()
	{
		DefaultHttpContext http = new();
		bool ok = await EndpointAuth.AuthenticateAsync(
			http, new RejectingSessionFactory(), Throttle(), "1.2.3.4",
			new BackendCredentials("u", "bad"), NullLogger.Instance, CancellationToken.None);

		Assert.False(ok);
		Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
	}

	private sealed class ThrowingSessionFactory : IBackendSessionFactory
	{
		public Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct)
		{
			throw new BackendException("mail backend unreachable");
		}

		public Task<IBackendSession> GetSessionAsync(
			BackendCredentials credentials, string deviceId, CancellationToken ct)
		{
			throw new NotSupportedException();
		}
	}

	private sealed class RejectingSessionFactory : IBackendSessionFactory
	{
		public Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct)
		{
			return Task.FromResult(false);
		}

		public Task<IBackendSession> GetSessionAsync(
			BackendCredentials credentials, string deviceId, CancellationToken ct)
		{
			throw new NotSupportedException();
		}
	}
}
