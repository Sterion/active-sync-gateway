using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

// Slim `eas`: forward the command line to the running gateway's loopback /cli endpoint so everyday
// verbs run against warm services instead of paying a cold start of the full server app. `serve` and
// `protect` (the full app's pre-parse specials, which accept arbitrary --Section:Key=value overrides
// a strict parser would reject) always run locally; EAS_NO_FORWARD=1 forces everything local. When no
// gateway answers, fall back to running the full app locally so server-less/repair verbs still work.

string[] arguments = args;
bool forceLocal = Environment.GetEnvironmentVariable("EAS_NO_FORWARD") == "1";
bool localOnly = arguments.Length > 0
	&& (Eq(arguments[0], "serve") || Eq(arguments[0], "protect"));

if (forceLocal || localOnly)
	return RunLocal(arguments, stdin: null);

// Read piped stdin once, up front: it feeds the forward, and is replayed to the local fallback.
string? stdin = Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : null;

string baseUrl = ResolveBaseUrl();
try
{
	using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
	using HttpResponseMessage response = await http.PostAsJsonAsync(
		$"{baseUrl}/cli", new CliRequest(arguments, stdin));
	if (response.IsSuccessStatusCode)
	{
		CliResponse? result = await response.Content.ReadFromJsonAsync<CliResponse>();
		if (result is not null)
		{
			if (result.Stdout.Length > 0)
				Console.Out.Write(result.Stdout);
			if (result.Stderr.Length > 0)
				Console.Error.Write(result.Stderr);
			return result.ExitCode;
		}
	}
	// 404 (endpoint disabled or the caller wasn't loopback) or an unparsable body → run locally.
}
catch (HttpRequestException)
{
	// No gateway listening (server stopped, or repairing an unconfigured one) → run locally.
}
catch (TaskCanceledException)
{
	// Request timed out → run locally rather than leave the operator without an answer.
}

return RunLocal(arguments, stdin);

static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

static string ResolveBaseUrl()
{
	// Same derivation the container HEALTHCHECK uses, plus a fallback read of the co-located
	// appsettings.json (a port set only in the file, not via env). Target 127.0.0.1, never
	// "localhost": the gateway listens IPv4-only (0.0.0.0), so a "localhost" that resolves to ::1
	// first makes the client wait out a failed IPv6 connect (~2 s) before retrying IPv4.
	string url = Environment.GetEnvironmentVariable("Kestrel__Endpoints__Http__Url")
		?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0]
		?? ReadAppSettingsUrl()
		?? "http://127.0.0.1:5080";
	return url.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1")
		.Replace("://localhost", "://127.0.0.1").TrimEnd('/');
}

static string? ReadAppSettingsUrl()
{
	try
	{
		string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
		if (!File.Exists(path))
			return null;
		using FileStream file = File.OpenRead(path);
		using JsonDocument doc = JsonDocument.Parse(file);
		if (doc.RootElement.TryGetProperty("Kestrel", out JsonElement kestrel)
			&& kestrel.TryGetProperty("Endpoints", out JsonElement endpoints)
			&& endpoints.TryGetProperty("Http", out JsonElement http)
			&& http.TryGetProperty("Url", out JsonElement value)
			&& value.ValueKind == JsonValueKind.String)
			return value.GetString();
	}
	catch
	{
		// A missing or malformed appsettings.json just means we fall through to the default.
	}
	return null;
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

internal sealed record CliRequest(string[] Args, string? Stdin);

internal sealed record CliResponse(int ExitCode, string Stdout, string Stderr);
