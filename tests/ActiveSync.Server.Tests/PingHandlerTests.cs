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

	// F16: the watcher detects a change but the returned backend key maps to no watched collection
	// (a store reporting at coarser granularity). The change must surface, not be silently dropped.
	[Fact]
	public async Task Ping_ChangeWithAnUnmappedBackendKey_IsNotSilentlyDropped()
	{
		_harness.Options.Eas.MinHeartbeatSeconds = 1; // keep the "no change" (red) path bounded
		UserFolder inbox = await InboxAsync();
		_harness.Session.Store.WaitForChanges = _ => new[] { "imap:SomethingElseEntirely" };

		PingHandler handler = NewHandler();
		XDocument? response = await _harness.RunAsync(handler, "Ping",
			PingRequest(new[] { inbox.ServerId }, heartbeat: 2));

		Assert.Equal("2", Status(response)); // reported as a change, not lost to the heartbeat
		List<string> reported = response!.Root!.Element(P + "Folders")!
			.Elements(P + "Folder").Select(f => f.Value).ToList();
		Assert.Contains(inbox.ServerId, reported);
	}

	// F18: monitoring more folders than the configured cap is refused with Ping Status 6 and the
	// limit, rather than silently spinning up an unbounded fan of watchers.
	[Fact]
	public async Task Ping_MonitoringMoreThanTheCap_IsRefusedWithStatus6()
	{
		_harness.Options.Eas.MaxPingFolders = 2;
		_harness.Options.Eas.MinHeartbeatSeconds = 1; // keep the un-capped (red) path from idling long
		_harness.Session.Store.WaitForChanges = _ => Array.Empty<string>();
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email),
			new BackendFolder("imap:A", "A", null, EasFolderType.UserMail, EasClass.Email),
			new BackendFolder("imap:B", "B", null, EasFolderType.UserMail, EasClass.Email));

		PingHandler handler = NewHandler();
		XDocument? response = await _harness.RunAsync(handler, "Ping",
			PingRequest(registry.Select(f => f.ServerId), heartbeat: 2));

		Assert.Equal("6", Status(response));
		Assert.Equal("2", response!.Root!.Element(P + "MaxFolders")?.Value);
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
			TestOptionsMonitor.Of(_harness.Options),
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
