using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using ActiveSync.Crypto;

namespace ActiveSync.Cli;

/// <summary>
///   The testable core of the slim <c>eas</c> forwarding client — see Program.cs's header comment for
///   the full behavioral rationale (sealed-envelope auth, the loopback-only target, the local fallback).
///   This type exists so that rationale has a seam to test against (S8): Program.cs stays a thin
///   top-level entry point that reads real console/environment state once and calls <see cref="RunAsync" />.
/// </summary>
internal static class EasForwardingClient
{
	/// <summary><c>serve</c>/<c>protect</c> are the full app's pre-parse specials and always run locally.</summary>
	internal static bool IsLocalOnlyVerb(string[] arguments) =>
		arguments.Length > 0 && (Eq(arguments[0], "serve") || Eq(arguments[0], "protect"));

	/// <summary><c>EAS_NO_FORWARD=1</c> forces every command to run locally.</summary>
	internal static bool ShouldForceLocal(Func<string, string?> getEnvironmentVariable) =>
		getEnvironmentVariable("EAS_NO_FORWARD") == "1";

	private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	///   Loads the ActiveSync:Encryption master key the same way the server does, from an env var or the
	///   co-located appsettings.json. Returns null when no key is configured (AllowPlaintext mode) — the
	///   sealed-envelope path is then skipped entirely and the request falls back to plaintext.
	/// </summary>
	internal static byte[]? LoadKey(Func<string, string?> getEnvironmentVariable, string appDirectory)
	{
		string? keyValue = ConfigValue(getEnvironmentVariable, appDirectory,
			"ActiveSync__Encryption__Key", "ActiveSync", "Encryption", "Key");
		string? keyFile = ConfigValue(getEnvironmentVariable, appDirectory,
			"ActiveSync__Encryption__KeyFile", "ActiveSync", "Encryption", "KeyFile");
		if (string.IsNullOrWhiteSpace(keyValue) && string.IsNullOrWhiteSpace(keyFile))
			return null;
		return EncryptionKeyLoader.TryLoadKey(new EncryptionOptions { Key = keyValue, KeyFile = keyFile }, out _);
	}

	/// <summary>
	///   Same derivation the container HEALTHCHECK uses, plus a fallback read of the co-located
	///   appsettings.json (a port set only in the file, not via env). Always resolves to 127.0.0.1, never
	///   "localhost": the gateway listens IPv4-only (0.0.0.0), so a "localhost" that resolves to ::1 first
	///   makes the client wait out a failed IPv6 connect (~2 s) before retrying IPv4.
	/// </summary>
	internal static string ResolveBaseUrl(Func<string, string?> getEnvironmentVariable, string appDirectory)
	{
		string url = getEnvironmentVariable("Kestrel__Endpoints__Http__Url")
			?? getEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0]
			?? ConfigValue(getEnvironmentVariable, appDirectory, null, "Kestrel", "Endpoints", "Http", "Url")
			?? "http://127.0.0.1:5080";
		return url.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1")
			.Replace("://localhost", "://127.0.0.1").TrimEnd('/');
	}

	// Env var (when named) wins, else the nested value from the co-located appsettings.json.
	private static string? ConfigValue(
		Func<string, string?> getEnvironmentVariable, string appDirectory, string? envName, params string[] jsonPath)
	{
		if (envName is not null)
		{
			string? fromEnv = getEnvironmentVariable(envName);
			if (!string.IsNullOrWhiteSpace(fromEnv))
				return fromEnv;
		}

		try
		{
			string path = Path.Combine(appDirectory, "appsettings.json");
			if (!File.Exists(path))
				return null;
			using FileStream file = File.OpenRead(path);
			using JsonDocument doc = JsonDocument.Parse(file);
			JsonElement element = doc.RootElement;
			foreach (string segment in jsonPath)
			{
				if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
					return null;
			}
			return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
		}
		catch
		{
			// A missing or malformed appsettings.json just means we fall through to the default/env.
			return null;
		}
	}

	/// <summary>
	///   Builds the request body: sealed (AES-GCM, master key) when a key is configured, otherwise the
	///   plaintext fallback the server also accepts only in AllowPlaintext mode.
	/// </summary>
	internal static CliRequest BuildRequest(
		string[] arguments, string? stdin, byte[]? key, bool color, int width, DateTimeOffset now) =>
		key is null
			? new CliRequest(arguments, stdin, null, color, width)
			: new CliRequest(null, null,
				LocalCliEnvelope.Create(arguments, stdin, now.ToUnixTimeMilliseconds()).Seal(key), color, width);

	/// <summary>Runs a command locally: <c>dotnet ActiveSync.Server.dll &lt;args&gt;</c> next to this binary.</summary>
	internal static int RunLocal(string[] arguments, string? stdin, string appDirectory)
	{
		string dll = Path.Combine(appDirectory, "ActiveSync.Server.dll");
		ProcessStartInfo start = new() { FileName = "dotnet", UseShellExecute = false };
		start.ArgumentList.Add(dll);
		foreach (string argument in arguments)
			start.ArgumentList.Add(argument);
		// Only redirect stdin when we already consumed it for the (failed) forward; otherwise let the
		// child inherit the real stdin (e.g. `serve`, or an interactive secret prompt).
		if (stdin is not null)
			start.RedirectStandardInput = true;

		using Process process = Process.Start(start)
			?? throw new InvalidOperationException("Could not start 'dotnet ActiveSync.Server.dll'.");
		if (stdin is not null)
		{
			process.StandardInput.Write(stdin);
			process.StandardInput.Close();
		}
		process.WaitForExit();
		return process.ExitCode;
	}

	/// <summary>
	///   The full forwarding flow driven from real console/environment/filesystem state — see Program.cs's
	///   header comment for the behavioral contract. The pieces above are what a test drives directly.
	/// </summary>
	internal static async Task<int> RunAsync(string[] arguments)
	{
		string appDirectory = AppContext.BaseDirectory;
		Func<string, string?> getEnv = Environment.GetEnvironmentVariable;

		bool forceLocal = ShouldForceLocal(getEnv);
		bool localOnly = IsLocalOnlyVerb(arguments);
		if (forceLocal || localOnly)
			return RunLocal(arguments, stdin: null, appDirectory);

		// Read piped stdin once, up front: it feeds the forward, and is replayed to the local fallback.
		string? stdin = Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : null;

		// Ask the gateway to render with ANSI colour + our terminal width when our stdout is a real
		// terminal that wants colour (a TTY, NO_COLOR unset) — piped/redirected output stays plain.
		bool color = !Console.IsOutputRedirected && string.IsNullOrEmpty(getEnv("NO_COLOR"));
		int width = 0;
		try
		{
			if (!Console.IsOutputRedirected)
				width = Console.WindowWidth;
		}
		catch
		{
			// No attached console (width stays 0 → the gateway uses a wide default).
		}

		byte[]? key = LoadKey(getEnv, appDirectory);
		CliRequest request = BuildRequest(arguments, stdin, key, color, width, DateTimeOffset.UtcNow);

		string baseUrl = ResolveBaseUrl(getEnv, appDirectory);
		try
		{
			using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
			using HttpResponseMessage response = await http.PostAsJsonAsync($"{baseUrl}/cli", request);
			if (response.IsSuccessStatusCode)
			{
				CliResponse? result = await response.Content.ReadFromJsonAsync<CliResponse>();
				if (result is not null)
				{
					// A sealed result is the keyed path: open it with the same key we sealed the request
					// with. The command has already RUN, so a failure to open must not fall through to the
					// local re-execution below — that would repeat a mutating verb.
					if (result.Sealed is not null)
					{
						if (key is null || !LocalCliResult.TryOpen(result.Sealed, key, out LocalCliResult? opened) || opened is null)
						{
							await Console.Error.WriteLineAsync(
								"eas: the gateway's response could not be decrypted (the master key changed mid-command?). " +
								"The command may have already run — do not simply retry it.");
							return 1;
						}
						result = new CliResponse(opened.ExitCode, opened.Stdout, opened.Stderr, null);
					}

					if (result.Stdout.Length > 0)
						await Console.Out.WriteAsync(result.Stdout);
					if (result.Stderr.Length > 0)
						await Console.Error.WriteAsync(result.Stderr);
					return result.ExitCode;
				}

				// A 2xx with an unreadable body: the command RAN server-side (success status), so
				// re-running it locally could repeat a mutating verb. Report and fail, don't fall back.
				await Console.Error.WriteLineAsync(
					"eas: the gateway returned an unreadable response; the command may have already run — do not simply retry it.");
				return 1;
			}

			// ONLY 404 proves the request never reached the CLI pipeline (endpoint disabled, non-loopback,
			// a rejected envelope, or — K7 — a credential-bearing verb refused because there is no master
			// key to seal its response with) — nothing ran, so local execution is safe. Any other status (a
			// 5xx especially) means the command may have started server-side and even completed its DB
			// writes; re-running it here would risk a live double-execution (L36).
			if (response.StatusCode == HttpStatusCode.NotFound)
				return RunLocal(arguments, stdin, appDirectory);

			await Console.Error.WriteLineAsync(
				$"eas: the gateway returned {(int)response.StatusCode} {response.ReasonPhrase}; the command " +
				"may have already run server-side, so it is not being retried locally.");
			return 1;
		}
		catch (HttpRequestException)
		{
			// No gateway listening (server stopped, or repairing an unconfigured one) → nothing ran, so run
			// locally.
			return RunLocal(arguments, stdin, appDirectory);
		}
		catch (TaskCanceledException)
		{
			// The 5-minute client timeout fired: the command is very likely still running server-side, so
			// re-running it locally would double-execute it. Report and fail instead (L36).
			await Console.Error.WriteLineAsync(
				"eas: the gateway did not respond within the timeout; the command may still be running " +
				"server-side, so it is not being retried locally.");
			return 1;
		}
	}
}

internal sealed record CliRequest(string[]? Args, string? Stdin, string? Sealed, bool Color, int Width);

internal sealed record CliResponse(int ExitCode, string Stdout, string Stderr, string? Sealed);
