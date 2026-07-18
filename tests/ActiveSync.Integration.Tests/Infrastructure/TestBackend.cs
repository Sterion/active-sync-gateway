using System.Net.Sockets;

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
	///   JMAP-groupware server (calendars + contacts) — the 0.13 default stack doesn't advertise
	///   those capabilities, so this points at the 0.16 stack under
	///   docker/backends/stalwart-jmap (single admin account, bootstrap mode).
	/// </summary>
	public static string JmapGroupwareUrl { get; } = Env("AS_TEST_JMAP_GROUPWARE_URL", "http://localhost:18080");

	public static string JmapGroupwareUser { get; } = Env("AS_TEST_JMAP_GROUPWARE_USER", "admin");
	public static string JmapGroupwarePassword { get; } = Env("AS_TEST_JMAP_GROUPWARE_PASSWORD", "secret");

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
			HttpRequestMessage request = new(
				HttpMethod.Get, new Uri(new Uri(TestBackend.JmapGroupwareUrl), "/.well-known/jmap"));
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
			       "(start docker/backends/stalwart-jmap) — " + ex.GetBaseException().Message;
		}
	});

	public JmapGroupwareFactAttribute()
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
