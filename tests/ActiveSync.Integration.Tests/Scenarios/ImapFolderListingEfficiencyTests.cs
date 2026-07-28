using ActiveSync.Backends.Imap;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Round 3, item 35 — <c>G14</c>/<c>G15</c>: folder-listing round-trip counts against a real IMAP
///   backend, both counted through the wire logger's client "LIST" lines (the same technique item
///   24's <c>ConnectionCountingLogger</c> uses for connects).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class ImapFolderListingEfficiencyTests
{
	private static ImapOptions Options => new()
	{
		Host = TestBackend.ImapHost,
		Port = TestBackend.ImapPort,
		UseSsl = false,
		Security = "None"
	};

	/// <summary>
	///   G14: <c>ListFoldersAsync.Walk</c> issued one LIST per folder (a non-recursive
	///   <c>GetSubfoldersAsync</c> call at every level), all under the session gate — the command
	///   count SCALED with the number of folders. A single namespace-wide LIST costs the same no
	///   matter how many folders exist (MailKit's own <c>GetFoldersAsync</c> still issues a small,
	///   CONSTANT number of LIST commands of its own — an INBOX probe plus a SPECIAL-USE pre-scan —
	///   so the invariant this proves is "constant", not "exactly one"). Proven by measuring the LIST
	///   command count against the built-in mailboxes, then again after adding six more folders, and
	///   asserting the two counts are equal.
	/// </summary>
	[BackendFact]
	public async Task ListFoldersAsync_ListCommandCount_DoesNotScaleWithFolderCount()
	{
		string user = TestBackend.User1;
		CancellationToken ct = CancellationToken.None;

		int baseline = await CountListCommandsAsync(user, ct);

		using (ImapClient raw = new())
		{
			await raw.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None, ct);
			await raw.AuthenticateAsync(user, TestBackend.Password, ct);
			IMailFolder personal = raw.GetFolder(raw.PersonalNamespaces[0]);
			for (int i = 0; i < 6; i++)
				await personal.CreateAsync($"ListG14-{Guid.NewGuid():N}", true, ct);
			await raw.DisconnectAsync(true, ct);
		}

		int afterSixMoreFolders = await CountListCommandsAsync(user, ct);

		Assert.Equal(baseline, afterSixMoreFolders);
	}

	private static async Task<int> CountListCommandsAsync(string user, CancellationToken ct)
	{
		ListCommandCountingLogger wire = new();
		ImapSession session = new(Options, new BackendCredentials(user, TestBackend.Password), NullLogger.Instance, wire);
		try
		{
			ImapMailBackend backend = new(session, user, _ => null, NullLogger.Instance);
			await backend.ListFoldersAsync(ct);
			return wire.ListCommands;
		}
		finally
		{
			await session.DisposeAsync();
		}
	}

	/// <summary>
	///   Counts IMAP LIST commands by watching for the wire logger's client ("C:") lines containing
	///   "LIST". Trace must be enabled (<c>IsEnabled</c> returns true) or
	///   <c>ImapConnectionFactory</c> does not attach the wire logger at all.
	/// </summary>
	private sealed class ListCommandCountingLogger : ILogger
	{
		private int _listCommands;

		public int ListCommands => Volatile.Read(ref _listCommands);

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(
			LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			string line = formatter(state, exception);
			if (line.Contains("] C:", StringComparison.Ordinal) &&
			    line.Contains("LIST", StringComparison.Ordinal))
				Interlocked.Increment(ref _listCommands);
		}
	}
}
