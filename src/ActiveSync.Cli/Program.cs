using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Crypto;

// Slim `eas`: forward the command line to the running gateway's loopback /cli endpoint so everyday
// verbs run against warm services instead of paying a cold start of the full server app. `serve` and
// `protect` (the full app's pre-parse specials, which accept arbitrary --Section:Key=value overrides
// a strict parser would reject) always run locally; EAS_NO_FORWARD=1 forces everything local. When no
// gateway answers, fall back to running the full app locally so server-less/repair verbs still work.
//
// The request is SEALED with the ActiveSync:Encryption master key (read from the same config the
// server uses): possessing the key is the real auth — a co-located Kubernetes sidecar or host-network
// peer that shares loopback but NOT the key can't call /cli. Falls back to a plain body only when no
// key is configured (AllowPlaintext dev/test), where the server also relies on loopback alone. The
// RESPONSE is sealed the same way whenever a key exists — command output carries secrets too.

string[] arguments = args;
bool forceLocal = Environment.GetEnvironmentVariable("EAS_NO_FORWARD") == "1";
bool localOnly = arguments.Length > 0
	&& (Eq(arguments[0], "serve") || Eq(arguments[0], "protect"));

if (forceLocal || localOnly)
	return RunLocal(arguments, stdin: null);

// Read piped stdin once, up front: it feeds the forward, and is replayed to the local fallback.
string? stdin = Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : null;

// Ask the gateway to render with ANSI colour + our terminal width when our stdout is a real
// terminal that wants colour (a TTY, NO_COLOR unset) — piped/redirected output stays plain.
bool color = !Console.IsOutputRedirected
	&& string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
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

byte[]? key = LoadKey();
CliRequest request = key is null
	? new CliRequest(arguments, stdin, null, color, width)
	: new CliRequest(null, null, LocalCliEnvelope.Create(
		arguments, stdin, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Seal(key), color, width);

string baseUrl = ResolveBaseUrl();
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
					Console.Error.WriteLine(
						"eas: the gateway's response could not be decrypted (the master key changed mid-command?). " +
						"The command may have already run — do not simply retry it.");
					return 1;
				}
				result = new CliResponse(opened.ExitCode, opened.Stdout, opened.Stderr, null);
			}

			if (result.Stdout.Length > 0)
				Console.Out.Write(result.Stdout);
			if (result.Stderr.Length > 0)
				Console.Error.Write(result.Stderr);
			return result.ExitCode;
		}

		// A 2xx with an unreadable body: the command RAN server-side (success status), so re-running
		// it locally could repeat a mutating verb. Report and fail, don't fall back.
		Console.Error.WriteLine(
			"eas: the gateway returned an unreadable response; the command may have already run — do not simply retry it.");
		return 1;
	}

	// ONLY 404 proves the request never reached the CLI pipeline (endpoint disabled, non-loopback,
	// or a rejected envelope) — nothing ran, so local execution is safe. Any other status (a 5xx
	// especially) means the command may have started server-side and even completed its DB writes;
	// re-running it here would risk a live double-execution (L36).
	if (response.StatusCode == HttpStatusCode.NotFound)
		return RunLocal(arguments, stdin);

	Console.Error.WriteLine(
		$"eas: the gateway returned {(int)response.StatusCode} {response.ReasonPhrase}; the command " +
		"may have already run server-side, so it is not being retried locally.");
	return 1;
}
catch (HttpRequestException)
{
	// No gateway listening (server stopped, or repairing an unconfigured one) → nothing ran, so run
	// locally.
	return RunLocal(arguments, stdin);
}
catch (TaskCanceledException)
{
	// The 5-minute client timeout fired: the command is very likely still running server-side, so
	// re-running it locally would double-execute it. Report and fail instead (L36).
	Console.Error.WriteLine(
		"eas: the gateway did not respond within the timeout; the command may still be running " +
		"server-side, so it is not being retried locally.");
	return 1;
}

static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

static byte[]? LoadKey()
{
	string? keyValue = ConfigValue("ActiveSync__Encryption__Key", "ActiveSync", "Encryption", "Key");
	string? keyFile = ConfigValue("ActiveSync__Encryption__KeyFile", "ActiveSync", "Encryption", "KeyFile");
	if (string.IsNullOrWhiteSpace(keyValue) && string.IsNullOrWhiteSpace(keyFile))
		return null;
	return EncryptionKeyLoader.TryLoadKey(new EncryptionOptions { Key = keyValue, KeyFile = keyFile }, out _);
}

static string ResolveBaseUrl()
{
	// Same derivation the container HEALTHCHECK uses, plus a fallback read of the co-located
	// appsettings.json (a port set only in the file, not via env). Target 127.0.0.1, never
	// "localhost": the gateway listens IPv4-only (0.0.0.0), so a "localhost" that resolves to ::1
	// first makes the client wait out a failed IPv6 connect (~2 s) before retrying IPv4.
	string url = Environment.GetEnvironmentVariable("Kestrel__Endpoints__Http__Url")
		?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0]
		?? ConfigValue(null, "Kestrel", "Endpoints", "Http", "Url")
		?? "http://127.0.0.1:5080";
	return url.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1")
		.Replace("://localhost", "://127.0.0.1").TrimEnd('/');
}

// Env var (when named) wins, else the nested value from the co-located appsettings.json.
static string? ConfigValue(string? envName, params string[] jsonPath)
{
	if (envName is not null)
	{
		string? fromEnv = Environment.GetEnvironmentVariable(envName);
		if (!string.IsNullOrWhiteSpace(fromEnv))
			return fromEnv;
	}

	try
	{
		string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
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

static int RunLocal(string[] arguments, string? stdin)
{
	string dll = Path.Combine(AppContext.BaseDirectory, "ActiveSync.Server.dll");
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

internal sealed record CliRequest(string[]? Args, string? Stdin, string? Sealed, bool Color, int Width);

internal sealed record CliResponse(int ExitCode, string Stdout, string Stderr, string? Sealed);
