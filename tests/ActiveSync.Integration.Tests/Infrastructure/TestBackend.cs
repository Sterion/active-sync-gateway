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
