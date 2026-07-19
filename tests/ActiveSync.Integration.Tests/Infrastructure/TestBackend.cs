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

	/// <summary>Radicale needs an explicit home set; Stalwart supports RFC 6764 discovery.</summary>
	public static string DavHomeSetPath { get; } =
		Env("AS_TEST_DAV_HOMESET", Stack.Equals("mailserver", StringComparison.OrdinalIgnoreCase) ? "/{user}/" : "");

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

/// <summary>A [Fact] that runs only when a JMAP-groupware server (calendars + contacts) is reachable.</summary>
public sealed class JmapGroupwareFactAttribute : FactAttribute
{
	private static readonly Lazy<string?> Reason = new(() =>
	{
		try
		{
			// Synchronous HttpClient.Send + ReadAsStream: this runs in the xunit discovery path
			// (attribute ctor), where blocking on async would trip the threading analyzer.
			using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
			string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
				$"{TestBackend.JmapGroupwareUser}:{TestBackend.JmapGroupwarePassword}"));
			// The session resource directly (not /.well-known/jmap): HttpClient strips the
			// Authorization header across the well-known redirect, which would 401 the probe.
			HttpRequestMessage request = new(
				HttpMethod.Get, new Uri(new Uri(TestBackend.JmapGroupwareUrl), "/jmap/session"));
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
			using HttpResponseMessage response = http.Send(request);
			using StreamReader reader = new(response.Content.ReadAsStream());
			string body = reader.ReadToEnd();
			return body.Contains("urn:ietf:params:jmap:contacts") && body.Contains("urn:ietf:params:jmap:calendars")
				? null
				: $"JMAP-groupware server at {TestBackend.JmapGroupwareUrl} lacks calendars/contacts capabilities.";
		}
		catch (Exception ex)
		{
			return $"No JMAP-groupware server at {TestBackend.JmapGroupwareUrl} " +
			       "(start docker/backends/stalwart) — " + ex.GetBaseException().Message;
		}
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
		try
		{
			// Synchronous HttpClient.Send + ReadAsStream: this runs in the xunit discovery path
			// (attribute ctor), where blocking on async would trip the threading analyzer.
			using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
			string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
				$"{TestBackend.User1}:{TestBackend.Password}"));
			// The session resource directly (not /.well-known/jmap): HttpClient strips the
			// Authorization header across the well-known redirect, which would 401 the probe.
			HttpRequestMessage request = new(HttpMethod.Get, new Uri(new Uri(url), "/jmap/session"));
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
			using HttpResponseMessage response = http.Send(request);
			using StreamReader reader = new(response.Content.ReadAsStream());
			string body = reader.ReadToEnd();
			return body.Contains("urn:ietf:params:jmap:mail")
			       && body.Contains("urn:ietf:params:jmap:submission")
			       && body.Contains("urn:ietf:params:jmap:vacationresponse")
				? null
				: $"JMAP server at {url} lacks mail/submission/vacation capabilities.";
		}
		catch (Exception ex)
		{
			return $"No JMAP mail server at {url} " +
			       "(start docker/backends/stalwart) — " + ex.GetBaseException().Message;
		}
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
