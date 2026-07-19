using System.Net.Sockets;
using System.Xml.Linq;

namespace ActiveSync.Integration.Tests.Infrastructure;

/// <summary>
///   Backend endpoint resolution for integration tests. Defaults target a stack running on
///   localhost (Visual Studio / host IDE flow); the devcontainer and CI compose files override
///   via environment variables. Both supported stacks publish the same ports and users.
/// </summary>
public static class TestBackend
{
	public const string User1 = "user1@example.com";
	public const string User2 = "user2@example.com";
	public const string Password = "pass";

	public static string Stack { get; } = Env("AS_TEST_STACK", "stalwart");
	public static string ImapHost { get; } = Env("AS_TEST_IMAP_HOST", "localhost");
	public static int ImapPort { get; } = int.Parse(Env("AS_TEST_IMAP_PORT", "143"));
	public static string SmtpHost { get; } = Env("AS_TEST_SMTP_HOST", ImapHost);
	public static int SmtpPort { get; } = int.Parse(Env("AS_TEST_SMTP_PORT", "587"));
	public static string? DavUrl { get; } = EnvOrNull("AS_TEST_DAV_URL") ?? $"http://{ImapHost}:5232";

	/// <summary>
	///   JMAP session base URL (Stalwart serves JMAP on the same HTTP listener as DAV). Defaults
	///   to the DAV URL so a single local stack covers both; CI/devcontainer may override.
	/// </summary>
	public static string? JmapUrl { get; } = EnvOrNull("AS_TEST_JMAP_URL") ?? DavUrl;

	/// <summary>
	///   JMAP-groupware endpoint (calendars + contacts). Stalwart 0.16 serves the full JMAP
	///   surface — mail, calendars, contacts, vacation — on the same HTTP listener as DAV, so
	///   the single default stack covers it; these default to that stack and a real test user.
	/// </summary>
	public static string JmapGroupwareUrl { get; } =
		Env("AS_TEST_JMAP_GROUPWARE_URL", JmapUrl ?? $"http://{ImapHost}:5232");

	public static string JmapGroupwareUser { get; } = Env("AS_TEST_JMAP_GROUPWARE_USER", User1);
	public static string JmapGroupwarePassword { get; } = Env("AS_TEST_JMAP_GROUPWARE_PASSWORD", Password);

	/// <summary>ManageSieve (Oof) — Stalwart only; the mailserver stack has no sieve listener.</summary>
	public static string SieveHost { get; } = Env("AS_TEST_SIEVE_HOST", ImapHost);

	public static int SievePort { get; } = int.Parse(Env("AS_TEST_SIEVE_PORT", "4190"));

	/// <summary>
	///   Whether the ManageSieve backend requires STARTTLS before auth (Stalwart does). Cyrus
	///   timsieved offers no STARTTLS, so its leg sets AS_TEST_SIEVE_TLS=false for plaintext.
	/// </summary>
	public static bool SieveUseTls { get; } =
		Env("AS_TEST_SIEVE_TLS", "true").Equals("true", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	///   Calendar/tasks home-set template. Radicale needs an explicit home set; Stalwart supports
	///   RFC 6764 discovery (empty). Cyrus/Axigen set it via AS_TEST_DAV_HOMESET.
	/// </summary>
	public static string DavHomeSetPath { get; } =
		Env("AS_TEST_DAV_HOMESET", Stack.Equals("mailserver", StringComparison.OrdinalIgnoreCase) ? "/{user}/" : "");

	/// <summary>
	///   Contacts (CardDAV) home-set template. Defaults to the calendar template so Stalwart and
	///   the mailserver/Radicale stack are unchanged; backends that root contacts elsewhere
	///   (Cyrus /dav/addressbooks/, Axigen /Contacts/) override via AS_TEST_DAV_CONTACTS_HOMESET.
	/// </summary>
	public static string DavContactsHomeSetPath { get; } =
		Env("AS_TEST_DAV_CONTACTS_HOMESET", DavHomeSetPath);

	/// <summary>
	///   MailSubmit provider for the shared gateway factory (default "smtp"). Cyrus has no SMTP
	///   submission MSA (LMTP-only) but does advertise JMAP submission, so its leg sets
	///   AS_TEST_MAILSUBMIT=jmap to exercise mail-flow over JMAP EmailSubmission instead.
	/// </summary>
	public static string MailSubmitProvider { get; } = Env("AS_TEST_MAILSUBMIT", "smtp");

	/// <summary>
	///   postgresql:// admin URI of a THROWAWAY PostgreSQL server. When set, every gateway
	///   factory creates (and later drops) its own database there instead of a SQLite temp
	///   file, so the Npgsql provider and its migrations are exercised end-to-end (CI sets
	///   this). Unreachable server = loud failure, never a silent SQLite fallback.
	/// </summary>
	public static string? PostgresUri { get; } = EnvOrNull("AS_TEST_PG");

	// Declared after the properties it reads: static-initializer nullability analysis
	// otherwise flags ImapHost as maybe-null inside the lambda (CS8604).
	private static readonly Lazy<string?> UnavailableReason = new(() =>
	{
		try
		{
			using TcpClient client = new();
			// Synchronous connect with a socket-level timeout: this runs inside the
			// xunit discovery path where async is unavailable (attribute constructors).
			client.SendTimeout = 3000;
			client.ReceiveTimeout = 3000;
			IAsyncResult result = client.BeginConnect(ImapHost, ImapPort, null, null);
			if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
				return $"No IMAP backend reachable at {ImapHost}:{ImapPort} (start a stack under docker/backends/).";
			client.EndConnect(result);
			return client.Connected
				? null
				: $"No IMAP backend reachable at {ImapHost}:{ImapPort} (start a stack under docker/backends/).";
		}
		catch (Exception ex)
		{
			return $"No IMAP backend reachable at {ImapHost}:{ImapPort} ({ex.GetBaseException().Message}).";
		}
	});

	public static bool IsAvailable => UnavailableReason.Value is null;
	public static string SkipReason => UnavailableReason.Value ?? "";

	private static string Env(string name, string fallback)
	{
		return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
	}

	private static string? EnvOrNull(string name)
	{
		return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
	}

	/// <summary>True when an SMTP submission MSA answers at SmtpHost:SmtpPort. Cyrus injects
	///   mail over LMTP only, so this is false there and the plain smtp warm-up/send is skipped.</summary>
	public static bool SmtpSubmissionAvailable => SmtpSubmissionProbe.Value;

	private static readonly Lazy<bool> SmtpSubmissionProbe = new(() =>
	{
		try
		{
			using TcpClient client = new();
			IAsyncResult result = client.BeginConnect(SmtpHost, SmtpPort, null, null);
			return result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)) && client.Connected;
		}
		catch
		{
			return false;
		}
	});

	/// <summary>
	///   True when the IMAP backend actually rejects a wrong password. The Cyrus test image
	///   authenticates every password (its saslauthd is a test mock), so auth-rejection
	///   scenarios cannot be exercised there and skip. Defaults to true (enforcing) when the
	///   probe can't reach the server — never hide a real failure behind a probe glitch.
	/// </summary>
	public static bool BackendEnforcesAuth => EnforcesAuthProbe.Value;

	private static readonly Lazy<bool> EnforcesAuthProbe = new(() =>
	{
		try
		{
			using TcpClient client = new() { SendTimeout = 3000, ReceiveTimeout = 3000 };
			IAsyncResult result = client.BeginConnect(ImapHost, ImapPort, null, null);
			if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)) || !client.Connected)
				return true;
			client.EndConnect(result);
			using NetworkStream stream = client.GetStream();
			byte[] buffer = new byte[1024];
			stream.Read(buffer, 0, buffer.Length); // untagged greeting
			byte[] login = System.Text.Encoding.ASCII.GetBytes(
				$"a login {User1} definitely-wrong-{Guid.NewGuid():N}\r\n");
			stream.Write(login, 0, login.Length);
			int read = stream.Read(buffer, 0, buffer.Length);
			string response = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
			return !response.Contains("a OK", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return true;
		}
	});

	/// <summary>
	///   Fetches a JMAP session resource via RFC 8620 discovery: GET {baseUrl}/.well-known/jmap
	///   and follow redirects (Stalwart → /jmap/session, Cyrus → /jmap/), re-attaching Basic auth
	///   on every hop because HttpClient strips it across redirects. Returns the session JSON, or
	///   null if no JMAP server answers. Synchronous — runs in the xunit discovery path.
	/// </summary>
	public static string? TryFetchJmapSession(string baseUrl, string user, string password)
	{
		try
		{
			string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
			using HttpClientHandler handler = new() { AllowAutoRedirect = false };
			using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
			Uri baseUri = new(baseUrl);
			Uri target = new(baseUri, "/.well-known/jmap");
			for (int hop = 0; hop < 4; hop++)
			{
				using HttpRequestMessage request = new(HttpMethod.Get, target);
				request.Headers.Authorization =
					new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
				using HttpResponseMessage response = http.Send(request);
				if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
				{
					target = new Uri(baseUri, location);
					continue;
				}

				if (!response.IsSuccessStatusCode)
					return null;
				using StreamReader reader = new(response.Content.ReadAsStream());
				return reader.ReadToEnd();
			}

			return null;
		}
		catch
		{
			return null;
		}
	}
}

/// <summary>A [Fact] that runs only when a real backend stack is reachable.</summary>
public sealed class BackendFactAttribute : FactAttribute
{
	public BackendFactAttribute()
	{
		if (!TestBackend.IsAvailable)
			Skip = TestBackend.SkipReason;
	}
}

/// <summary>
///   A [Fact] for auth-rejection scenarios: runs only when the IMAP backend actually rejects
///   a bad password. Skips on the Cyrus test image, whose saslauthd accepts every password.
/// </summary>
public sealed class BackendEnforcesAuthFactAttribute : FactAttribute
{
	public BackendEnforcesAuthFactAttribute()
	{
		if (!TestBackend.IsAvailable)
			Skip = TestBackend.SkipReason;
		else if (!TestBackend.BackendEnforcesAuth)
			Skip = "Backend authenticates any password (Cyrus test image) — auth rejection is not testable.";
	}
}

/// <summary>
///   A [Fact] for scenarios that need an SMTP submission MSA. Skips on backends with none
///   (Cyrus injects mail over LMTP only; its mail-flow is covered over JMAP submission).
/// </summary>
public sealed class SmtpSubmissionFactAttribute : FactAttribute
{
	public SmtpSubmissionFactAttribute()
	{
		if (!TestBackend.IsAvailable)
			Skip = TestBackend.SkipReason;
		else if (!TestBackend.SmtpSubmissionAvailable)
			Skip = $"No SMTP submission MSA at {TestBackend.SmtpHost}:{TestBackend.SmtpPort} (LMTP-only backend).";
	}
}

/// <summary>
///   A [Fact] that skips on a named stack whose server-side semantics make the scenario
///   inapplicable — used for genuine backend behavior differences with no cheap capability
///   probe (e.g. Cyrus's test image auto-schedules iMIP internally rather than emailing,
///   surfaces shared collections differently, and doesn't push IDLE on non-INBOX folders).
///   <paramref name="stack" /> may name several stacks as a comma-separated list when one
///   reason covers all of them (e.g. Baikal, like Cyrus, never emails an iMIP invitation into
///   the attendee's IMAP mailbox — its DAV server and the mail companion are separate systems).
/// </summary>
public sealed class SkipOnStackFactAttribute : FactAttribute
{
	public SkipOnStackFactAttribute(string stack, string reason)
	{
		if (!TestBackend.IsAvailable)
			Skip = TestBackend.SkipReason;
		else if (stack.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(s => TestBackend.Stack.Equals(s, StringComparison.OrdinalIgnoreCase)))
			Skip = reason;
	}
}

/// <summary>
///   A [Fact] for JMAP VacationResponse (Oof) round-trips: requires a JMAP mail server and
///   skips on Cyrus, whose VacationResponse state semantics differ from what RFC-8621 clients
///   (and this gateway) expect.
/// </summary>
public sealed class JmapVacationFactAttribute : FactAttribute
{
	public JmapVacationFactAttribute()
	{
		if (TestBackend.JmapUrl is not { } url)
		{
			Skip = "No JMAP endpoint configured (AS_TEST_JMAP_URL / AS_TEST_DAV_URL).";
			return;
		}

		string? body = TestBackend.TryFetchJmapSession(url, TestBackend.User1, TestBackend.Password);
		if (body is null || !body.Contains("urn:ietf:params:jmap:vacationresponse"))
			Skip = $"No JMAP VacationResponse capability at {url}.";
		else if (TestBackend.Stack.Equals("cyrus", StringComparison.OrdinalIgnoreCase))
			Skip = "Cyrus JMAP VacationResponse state semantics differ from RFC 8621 clients.";
	}
}

/// <summary>A [Fact] that runs only when a JMAP-groupware server (calendars + contacts) is reachable.</summary>
public sealed class JmapGroupwareFactAttribute : FactAttribute
{
	private static readonly Lazy<string?> Reason = new(() =>
	{
		string? body = TestBackend.TryFetchJmapSession(
			TestBackend.JmapGroupwareUrl, TestBackend.JmapGroupwareUser, TestBackend.JmapGroupwarePassword);
		if (body is null)
			return $"No JMAP-groupware server at {TestBackend.JmapGroupwareUrl} (start docker/backends/stalwart or cyrus).";
		return body.Contains("urn:ietf:params:jmap:contacts") && body.Contains("urn:ietf:params:jmap:calendars")
			? null
			: $"JMAP-groupware server at {TestBackend.JmapGroupwareUrl} lacks calendars/contacts capabilities.";
	});

	public JmapGroupwareFactAttribute()
	{
		if (Reason.Value is not null)
			Skip = Reason.Value;
	}
}

/// <summary>
///   A [Fact] that runs only when a JMAP <em>mail</em> server is reachable — mail store,
///   submission and vacation response on one session (Stalwart). Mail-only stacks such as
///   docker-mailserver have no JMAP listener, so the JMAP mail/Oof scenarios skip cleanly
///   instead of pointing the JMAP provider at a DAV-only endpoint and failing.
/// </summary>
public sealed class JmapMailFactAttribute : FactAttribute
{
	private static readonly Lazy<string?> Reason = new(() =>
	{
		if (TestBackend.JmapUrl is not { } url)
			return "No JMAP endpoint configured (AS_TEST_JMAP_URL / AS_TEST_DAV_URL).";
		string? body = TestBackend.TryFetchJmapSession(url, TestBackend.User1, TestBackend.Password);
		if (body is null)
			return $"No JMAP mail server at {url} (start docker/backends/stalwart or cyrus).";
		return body.Contains("urn:ietf:params:jmap:mail")
		       && body.Contains("urn:ietf:params:jmap:submission")
		       && body.Contains("urn:ietf:params:jmap:vacationresponse")
			? null
			: $"JMAP server at {url} lacks mail/submission/vacation capabilities.";
	});

	public JmapMailFactAttribute()
	{
		if (Reason.Value is not null)
			Skip = Reason.Value;
	}
}

/// <summary>
///   A [Fact] that runs only when the CalDAV backend advertises the CALDAV:free-busy-query
///   report in a calendar's supported-report-set. Radicale answers the report but neither
///   advertises it nor computes it exactly, so the CalDAV free/busy scenario skips there and
///   runs on servers that implement it properly (Stalwart). Only stacks configured with an
///   explicit DAV home-set template are probed; RFC 6764 discovery backends implement it.
/// </summary>
public sealed class CalDavFreeBusyFactAttribute : FactAttribute
{
	private static readonly XNamespace Dav = "DAV:";
	private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

	private static readonly Lazy<string?> Reason = new(() =>
	{
		if (TestBackend.DavUrl is not { } davUrl)
			return "No CalDAV backend configured.";
		// Empty home-set path => the backend supports RFC 6764 discovery (Stalwart), which
		// implements free-busy-query. Only the explicit-template stacks (Radicale) get probed.
		if (string.IsNullOrEmpty(TestBackend.DavHomeSetPath))
			return null;
		try
		{
			using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
			string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
				$"{TestBackend.User1}:{TestBackend.Password}"));
			http.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
			string home = TestBackend.DavHomeSetPath.Replace("{user}", TestBackend.User1);
			XDocument body = new(new XElement(Dav + "propfind",
				new XElement(Dav + "prop",
					new XElement(Dav + "resourcetype"),
					new XElement(Dav + "supported-report-set"))));
			// Synchronous Send: this runs in the xunit discovery path (attribute ctor).
			using HttpRequestMessage request = new(new HttpMethod("PROPFIND"), new Uri(new Uri(davUrl), home))
			{
				Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/xml")
			};
			request.Headers.Add("Depth", "1");
			using HttpResponseMessage response = http.Send(request);
			if ((int)response.StatusCode != 207)
				return null; // inconclusive — never skip a possibly-capable backend
			using StreamReader reader = new(response.Content.ReadAsStream());
			XDocument doc = XDocument.Parse(reader.ReadToEnd());
			bool sawCalendar = false;
			foreach (XElement resp in doc.Descendants(Dav + "response"))
			{
				if (!resp.Descendants(Dav + "resourcetype").Elements(CalDav + "calendar").Any())
					continue;
				sawCalendar = true;
				if (resp.Descendants(CalDav + "free-busy-query").Any())
					return null; // advertised — run
			}
			return sawCalendar
				? $"CalDAV server at {davUrl} does not advertise the free-busy-query report."
				: null;
		}
		catch
		{
			return null; // probe glitch — run rather than hide a real failure
		}
	});

	public CalDavFreeBusyFactAttribute()
	{
		if (Reason.Value is not null)
			Skip = Reason.Value;
	}
}

/// <summary>Polling helpers for eventually-consistent assertions (mail delivery, push).</summary>
public static class WaitUntil
{
	public static async Task<T> ResultAsync<T>(
		Func<Task<T?>> probe, string what, TimeSpan? timeout = null, TimeSpan? interval = null)
		where T : class
	{
		DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
		Exception? lastError = null;
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				T? result = await probe();
				if (result is not null)
					return result;
			}
			catch (Exception ex)
			{
				lastError = ex;
			}

			await Task.Delay(interval ?? TimeSpan.FromSeconds(1));
		}

		throw new TimeoutException($"Timed out waiting for {what}.", lastError);
	}

	public static Task TrueAsync(
		Func<Task<bool>> probe, string what, TimeSpan? timeout = null, TimeSpan? interval = null)
	{
		return ResultAsync(async () => await probe() ? "ok" : null, what, timeout, interval);
	}
}
