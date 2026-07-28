using System.Reflection;
using System.Threading;
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

	/// <summary>
	///   G11: <c>DisposeAsync</c> used to dispose the internal gate unconditionally. RunAsync's
	///   disposed-flag check and the gate wait/release are two separate steps, so a caller already
	///   past the flag check (or racing DisposeAsync's own 5 s bounded wait) can still touch the gate
	///   after it is torn down, surfacing a raw <see cref="ObjectDisposedException" /> instead of the
	///   documented <see cref="BackendException" />. Reproduced directly against the gate — racing the
	///   real thread interleaving is impractical here because MailKit's <see cref="ImapClient" /> has
	///   no fake/seam, so any call that reaches <c>EnsureConnectedAsync</c> attempts a real connection.
	/// </summary>
	[Fact]
	public async Task DisposeAsync_MustNotDisposeTheGate_SoARacingCallerNeverSeesObjectDisposed()
	{
		ImapSession session = new(
			new ImapOptions { Host = "localhost", Port = 143 },
			new BackendCredentials("user", "pass"),
			NullLogger.Instance);

		await session.DisposeAsync();

		SemaphoreSlim gate = (SemaphoreSlim)typeof(ImapSession)
			.GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(session)!;

		Exception? ex = Record.Exception(() => gate.Wait(0));
		Assert.Null(ex);
	}
}
