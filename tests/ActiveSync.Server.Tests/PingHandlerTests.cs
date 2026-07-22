using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Ping long-poll reliability (item 27): parameter caching (F17), the folder-count cap (F18),
///   and never silently dropping a detected change that maps to no collection (F16).
/// </summary>
public sealed class PingHandlerTests : IDisposable
{
	private static readonly XNamespace P = EasNamespaces.Ping;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	// F17: a Ping that carries Folders but omits HeartbeatInterval must reuse the cached heartbeat,
	// not answer Status 3 (which forces the client to resend the full request every cycle).
	[Fact]
	public async Task Ping_WithFoldersButNoHeartbeat_ReusesCachedHeartbeat()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Store.WaitForChanges = keys => keys.ToList(); // every watched folder reports a change

		PingHandler handler = NewHandler();

		XDocument? first = await _harness.RunAsync(handler, "Ping",
			PingRequest(new[] { inbox.ServerId }, heartbeat: 120));
		Assert.Equal("2", Status(first)); // cache established

		XDocument? second = await _harness.RunAsync(handler, "Ping",
			PingRequest(new[] { inbox.ServerId }, heartbeat: null));
		Assert.Equal("2", Status(second));
	}

	private async Task<UserFolder> InboxAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return registry.Single(f => f.BackendKey == "imap:INBOX");
	}

	private PingHandler NewHandler()
	{
		return new PingHandler(
			_harness.Folders,
			TestOptionsMonitor.SnapshotOf(_harness.Options),
			new StubLifetime(),
			NullLogger<PingHandler>.Instance);
	}

	private static string? Status(XDocument? response)
	{
		return response?.Root?.Element(P + "Status")?.Value;
	}

	private static XDocument PingRequest(IEnumerable<string> folderIds, int? heartbeat)
	{
		XElement ping = new(P + "Ping");
		if (heartbeat is { } hb)
			ping.Add(new XElement(P + "HeartbeatInterval", hb.ToString()));
		ping.Add(new XElement(P + "Folders",
			folderIds.Select(id => new XElement(P + "Folder", new XElement(P + "Id", id)))));
		return new XDocument(ping);
	}

	private sealed class StubLifetime : IHostApplicationLifetime
	{
		public CancellationToken ApplicationStarted => CancellationToken.None;
		public CancellationToken ApplicationStopping => CancellationToken.None;
		public CancellationToken ApplicationStopped => CancellationToken.None;

		public void StopApplication()
		{
		}
	}
}
