using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D28: <see cref="ImapSession" /> disposal must not strand late callers with an
///   <see cref="ObjectDisposedException" /> from the disposed gate — a caller arriving after
///   disposal gets a clean <see cref="BackendException" /> instead.
/// </summary>
public class ImapSessionTests
{
	[Fact]
	public async Task RunAsync_AfterDispose_ThrowsBackendException_NotObjectDisposed()
	{
		ImapSession session = new(
			new ImapOptions { Host = "localhost", Port = 143 },
			new BackendCredentials("user", "pass"),
			NullLogger.Instance);

		await session.DisposeAsync();

		// A late RunAsync must fail cleanly; the fake action must never run (no real connect).
		await Assert.ThrowsAsync<BackendException>(() =>
			session.RunAsync((ImapClient _) => Task.CompletedTask, CancellationToken.None));
	}

	[Fact]
	public async Task Dispose_IsIdempotent()
	{
		ImapSession session = new(
			new ImapOptions { Host = "localhost", Port = 143 },
			new BackendCredentials("user", "pass"),
			NullLogger.Instance);

		await session.DisposeAsync();
		await session.DisposeAsync(); // must not throw ObjectDisposedException on the gate
	}
}
