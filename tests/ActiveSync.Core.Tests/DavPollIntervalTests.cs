using System.Diagnostics;
using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H11 (coverage — the plumbing is a new seam, so it cannot be observed red-first through
///   behaviour: the old poller took no interval at all and always waited a hardcoded 60 s). The poll
///   interval passed to <see cref="DavDiscovery.PollCtagsAsync" /> is now the operator's
///   <c>Eas:DavPollSeconds</c>, so a change is detected at the configured cadence rather than after a
///   fixed minute. The test drives the poller with a 1 s interval against a ctag that changes after
///   the baseline read and asserts detection lands well inside a minute.
/// </summary>
public sealed class DavPollIntervalTests
{
	private static readonly Uri Base = new("https://dav.example.com/");

	[Fact]
	public async Task PollCtags_UsesConfiguredInterval_DetectsChangeWithoutWaitingAMinute()
	{
		// getctag reads "v1" on the baseline snapshot and "v2" on every poll afterwards.
		int reads = 0;
		CtagHandler stub = new(() => Interlocked.Increment(ref reads) <= 1 ? "v1" : "v2");
		using WebDavClient dav = new(Base, new HttpClient(stub));

		Stopwatch sw = Stopwatch.StartNew();
		IReadOnlyList<string> changed = await DavDiscovery.PollCtagsAsync(
			dav,
			["caldav:/dav/cal/"],
			key => key["caldav:".Length..],
			timeout: TimeSpan.FromSeconds(30),
			pollSeconds: 1,
			NullLogger.Instance,
			"CalDAV",
			"user",
			CancellationToken.None);
		sw.Stop();

		Assert.Equal(["caldav:/dav/cal/"], changed);
		// A 1 s cadence detects on the first poll (~1 s); the old hardcoded 60 s would have waited far
		// longer. A generous 15 s ceiling proves the configured interval, not the minute, is in force.
		Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
			$"change was not detected until {sw.Elapsed.TotalSeconds:F1}s — the poll interval is not honoured");
	}

	// H5: AGENTS.md documents the full-enumeration posture's cost bound as "one Depth:1 ctag
	// PROPFIND per home set, not per folder" (the H12 mitigation list) — but the poller issued one
	// PROPFIND (sometimes two, when getctag is absent) per WATCHED FOLDER. Five calendars sharing
	// one home set must therefore cost one PROPFIND per snapshot (baseline + one poll = 2 requests
	// total), not one per folder per snapshot (5 * 2 = 10 on the unmodified loop). A short,
	// generous-relative-to-pollSeconds timeout (900 ms with a 1 s poll interval) forces exactly one
	// poll iteration: Math.Max(1, remaining) always delays at least 1 s, so the 900 ms deadline is
	// exceeded before the loop re-checks, regardless of scheduler jitter.
	[Fact]
	public async Task PollCtags_MultipleFoldersUnderOneHomeSet_UsesOnePropfindPerHomeSet()
	{
		string[] hrefs =
			["/dav/home/cal1/", "/dav/home/cal2/", "/dav/home/cal3/", "/dav/home/cal4/", "/dav/home/cal5/"];
		string[] keys = hrefs.Select(h => "caldav:" + h).ToArray();

		int requestCount = 0;
		HomeSetHandler stub = new(() =>
		{
			Interlocked.Increment(ref requestCount);
			return HomeSetXml(hrefs, "same-ctag");
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));

		await DavDiscovery.PollCtagsAsync(
			dav,
			keys,
			key => key["caldav:".Length..],
			timeout: TimeSpan.FromMilliseconds(900),
			pollSeconds: 1,
			NullLogger.Instance,
			"CalDAV",
			"user",
			CancellationToken.None);

		Assert.True(requestCount <= 3,
			$"expected ~1 grouped PROPFIND per snapshot (<=3 total for baseline+poll), got {requestCount} " +
			"— folders sharing a home set are still being polled one PROPFIND per folder");
	}

	private static HttpResponseMessage Xml(string ctag)
	{
		string multistatus =
			$"""
			<D:multistatus xmlns:D="DAV:" xmlns:CS="http://calendarserver.org/ns/">
			  <D:response>
			    <D:href>/dav/cal/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><CS:getctag>{ctag}</CS:getctag></D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		return new HttpResponseMessage((HttpStatusCode)207)
		{
			Content = new StringContent(multistatus, Encoding.UTF8, "application/xml")
		};
	}

	private static HttpResponseMessage HomeSetXml(IReadOnlyList<string> hrefs, string ctag)
	{
		string responses = string.Join("\n", hrefs.Select(h => $"""
		<D:response>
		  <D:href>{h}</D:href>
		  <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
		    <D:prop><CS:getctag>{ctag}</CS:getctag></D:prop>
		  </D:propstat>
		</D:response>
		"""));
		string multistatus =
			$"""
			<D:multistatus xmlns:D="DAV:" xmlns:CS="http://calendarserver.org/ns/">
			{responses}
			</D:multistatus>
			""";
		return new HttpResponseMessage((HttpStatusCode)207)
		{
			Content = new StringContent(multistatus, Encoding.UTF8, "application/xml")
		};
	}

	private sealed class CtagHandler(Func<string> nextCtag) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(Xml(nextCtag()));
		}
	}

	private sealed class HomeSetHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(responder());
		}
	}
}
