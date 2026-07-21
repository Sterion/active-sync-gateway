using System.Net;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Crypto;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ActiveSync.Server.Cli;

/// <summary>
///   The loopback CLI-forwarding endpoint. The slim <c>eas</c> client POSTs a command line (+ stdin)
///   here so it runs against the already-warm gateway process instead of paying a cold .NET start;
///   the same Spectre command tree that a local <c>eas</c> would run executes in-process and its
///   stdout/stderr/exit-code are returned.
///
///   <para>Security: the real gate is proof of the <c>ActiveSync:Encryption</c> master key — the
///   caller must present a fresh <see cref="LocalCliEnvelope" /> sealed with it, which a co-located
///   sidecar sharing loopback but not the container's secrets cannot produce. Loopback is kept as a
///   cheap pre-filter, and it is trustworthy because the gateway installs no forwarded-headers
///   middleware, so <c>RemoteIpAddress</c> is the true transport peer. Loopback alone is accepted
///   ONLY when the operator explicitly set <c>Encryption:AllowPlaintext</c> (dev/test); a key that
///   is missing or fails to load disables the endpoint rather than degrading it. The request body is
///   never logged (<c>UseEasRequestLogging</c> records only method/path/status).</para>
/// </summary>
internal static class LocalCliEndpoint
{
	// Capturing output means swapping the process-global Console + AnsiConsole.Console, so only one
	// forwarded command may run at a time. CLI traffic is low-frequency interactive admin work, so a
	// single lock costs nothing in practice.
	private static readonly SemaphoreSlim Gate = new(1, 1);

	/// <summary>How far a sealed request's timestamp may be from now before it's treated as a replay.</summary>
	internal const long AuthWindowMs = 60_000;

	// Color/Width are rendering hints from the caller's terminal (not secret, so they ride outside
	// the sealed envelope). Color adds ANSI escapes to the output; Width wraps tables to the
	// caller's terminal. A tampering sidecar still can't get a response (it can't seal a request),
	// and at worst these only affect cosmetics of a legitimate caller's own output.
	internal sealed record CliRequest(string[]? Args, string? Stdin, string? Sealed, bool Color = false, int Width = 0);

	/// <summary>
	///   The forwarded command's result. When a master key is configured the payload rides in
	///   <see cref="Sealed" /> (see <see cref="ProtectResponse" />) and the plaintext fields are
	///   empty; in AllowPlaintext dev/test the plaintext fields carry it and <c>Sealed</c> is null.
	/// </summary>
	internal sealed record CliResponse(int ExitCode, string Stdout, string Stderr, string? Sealed = null);

	internal static void Map(WebApplication app)
	{
		// The master key is a restart-tier bootstrap setting, so derive it once.
		EncryptionOptions encryption =
			app.Services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value.Encryption;
		byte[]? key = EncryptionKeyLoader.TryLoadKey(encryption, out string? keyError);
		// Plaintext (loopback-only) auth is opened by the EXPLICIT AllowPlaintext flag, never by the
		// key merely being absent — a key that fails to load looks identical to "none configured",
		// and silently degrading to the model the design rejects is the worst possible default.
		bool allowPlaintext = key is null && encryption.AllowPlaintext;
		if (key is null)
		{
			if (allowPlaintext)
				app.Logger.LogWarning(
					"/cli is running with loopback-only authentication (ActiveSync:Encryption:AllowPlaintext " +
					"is set, so there is no master key to seal requests with). Any local process — including a " +
					"co-located sidecar sharing this network namespace — can run every eas command. Configure " +
					"ActiveSync:Encryption:Key for production.");
			else
				app.Logger.LogError(
					"/cli is DISABLED: no usable ActiveSync:Encryption key, and AllowPlaintext is not set, so " +
					"no caller can be authenticated. {Error}", keyError ?? "No key is configured.");
		}

		app.MapPost("/cli", async (HttpContext context, IOptionsMonitor<ActiveSyncOptions> options, CliRequest? request) =>
		{
			// Disabled or non-loopback: 404 so the endpoint is invisible. Loopback is a cheap
			// pre-filter; the real auth is proof of the master key (see TryAuthorize).
			if (!options.CurrentValue.Cli.Enabled || !IsLoopback(context.Connection.RemoteIpAddress))
				return Results.NotFound();
			if (!TryAuthorize(request, key, allowPlaintext, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				    out string[] args, out string stdin))
				return Results.NotFound();

			CliResponse response = await ExecuteAsync(args, stdin, context.RequestAborted,
				request?.Color ?? false, request?.Width ?? 0);
			return Results.Json(ProtectResponse(response, key));
		});
	}

	/// <summary>True only for a loopback transport peer (127.0.0.0/8 or ::1); null is never allowed.</summary>
	internal static bool IsLoopback(IPAddress? peer) => peer is not null && IPAddress.IsLoopback(peer);

	/// <summary>
	///   Authenticates a request as a master-key holder. With a key configured, the caller MUST
	///   supply a fresh <see cref="LocalCliEnvelope" /> sealed with it (a co-located sidecar without
	///   the key can't) — the plaintext args/stdin are ignored, and <paramref name="allowPlaintext" />
	///   cannot weaken that. Only when there is no key AND the operator explicitly set
	///   <c>ActiveSync:Encryption:AllowPlaintext</c> (dev/test) is a plain body accepted behind the
	///   loopback gate alone; a key that simply failed to load authenticates nobody.
	/// </summary>
	internal static bool TryAuthorize(
		CliRequest? request, byte[]? key, bool allowPlaintext, long nowUnixMs, out string[] args, out string stdin)
	{
		if (key is null && !allowPlaintext)
		{
			args = [];
			stdin = "";
			return false;
		}

		if (key is null)
		{
			args = request?.Args ?? [];
			stdin = request?.Stdin ?? "";
			return true;
		}

		if (!LocalCliEnvelope.TryOpen(request?.Sealed, key, nowUnixMs, AuthWindowMs, out LocalCliEnvelope? envelope)
			|| envelope is null)
		{
			args = [];
			stdin = "";
			return false;
		}

		args = envelope.Args;
		stdin = envelope.Stdin ?? "";
		return true;
	}

	/// <summary>
	///   Seals a captured result for the wire when a master key is configured. Command output is as
	///   sensitive as command input — <c>eas device password</c> discloses a live credential — and
	///   only a key holder could have got this far, so the same key protects the way back. With no
	///   key (AllowPlaintext dev/test) there is nothing to seal with and the result stays plain.
	/// </summary>
	internal static CliResponse ProtectResponse(CliResponse response, byte[]? key) =>
		key is null
			? response
			: new CliResponse(0, "", "",
				new LocalCliResult(response.ExitCode, response.Stdout, response.Stderr).Seal(key));

	/// <summary>
	///   Runs one CLI command line in-process and returns its captured output + exit code. Refuses
	///   <c>serve</c> (no nested gateway); every other verb — secret-bearing or not — is allowed.
	/// </summary>
	internal static async Task<CliResponse> ExecuteAsync(
		string[] args, string stdin, CancellationToken ct, bool color = false, int width = 0)
	{
		if (args.Length > 0 && string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
			return new CliResponse(1, "", "serve is not available over /cli; run it locally.\n");

		return await RunCapturedAsync(args, stdin, ct, color, width);
	}

	/// <summary>
	///   Runs the Spectre command tree with stdout/stderr/stdin redirected to per-request buffers.
	///   Commands emit through both the injected <see cref="IAnsiConsole" /> (default
	///   <see cref="AnsiConsole.Console" />) and the raw process-global <see cref="Console" />, so
	///   both are redirected under the lock and restored in <c>finally</c>.
	/// </summary>
	private static async Task<CliResponse> RunCapturedAsync(
		string[] args, string stdin, CancellationToken ct, bool color, int width)
	{
		await Gate.WaitAsync(ct);
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		TextReader originalIn = Console.In;
		IAnsiConsole originalAnsi = AnsiConsole.Console;
		StringWriter outWriter = new();
		StringWriter errorWriter = new();
		try
		{
			Console.SetOut(outWriter);
			Console.SetError(errorWriter);
			Console.SetIn(new StringReader(stdin));
			// Render with ANSI colour only when the CALLER's terminal wants it (a TTY, NO_COLOR
			// unset) — the client detects that and sends the hint, so piped/redirected output stays
			// plain. Pin the profile width to the caller's terminal (or a wide default) so tables
			// wrap correctly and never depend on this process's ambient console width.
			IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
			{
				Ansi = color ? AnsiSupport.Yes : AnsiSupport.No,
				ColorSystem = color ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
				Interactive = InteractionSupport.No,
				Out = new AnsiConsoleOutput(outWriter),
			});
			captured.Profile.Width = width > 0 ? width : 200;
			captured.Profile.Height = 100;
			// Static AnsiConsole.* helpers write here; commands that take an injected IAnsiConsole
			// get the same instance via CapturingRegistrar (Spectre's DEFAULT registrar caches the
			// console process-statically — the first forwarded command would otherwise capture all
			// later ones' output).
			AnsiConsole.Console = captured;

			CommandApp<BannerCommand> cli = new(new CapturingRegistrar(captured));
			cli.Configure(config =>
			{
				CliApp.Configure(config);
				// Command bodies get the captured console via the registrar, but Spectre renders help
				// (--help/-h), USAGE on a bare branch and parse errors (unknown command) through
				// Settings.Console, falling back to a process-static AnsiConsole.Console it caches on
				// FIRST use. In the long-lived gateway that cache pins the very first /cli command's
				// (now-dead) buffer, so every later help/error came back empty. Pin it explicitly.
				config.Settings.Console = captured;
			});
			int exitCode = await cli.RunAsync(args);
			return new CliResponse(exitCode, outWriter.ToString(), errorWriter.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Console.SetIn(originalIn);
			AnsiConsole.Console = originalAnsi;
			Gate.Release();
		}
	}

	/// <summary>
	///   Minimal Spectre DI bridge: resolves <see cref="IAnsiConsole" /> to the per-request captured
	///   console and constructs everything else by its longest constructor (recursively resolving
	///   parameters). Used instead of Spectre's default registrar, which caches the console in a
	///   process-static — fatal when successive forwarded commands each need their own buffer.
	/// </summary>
	private sealed class CapturingRegistrar(IAnsiConsole console) : ITypeRegistrar, ITypeResolver
	{
		private readonly Dictionary<Type, object> _instances = new() { [typeof(IAnsiConsole)] = console };
		private readonly Dictionary<Type, Type> _registrations = [];
		private readonly Dictionary<Type, Func<object?>> _factories = [];

		public void Register(Type service, Type implementation) => _registrations[service] = implementation;

		public void RegisterInstance(Type service, object implementation) => _instances[service] = implementation;

		public void RegisterLazy(Type service, Func<object> factory) => _factories[service] = factory;

		public ITypeResolver Build() => this;

		public object? Resolve(Type? type)
		{
			if (type is null)
				return null;
			if (_instances.TryGetValue(type, out object? instance))
				return instance;
			if (_factories.TryGetValue(type, out Func<object?>? factory))
				return factory();

			// Spectre resolves IEnumerable<T> of its internal extension points (help providers,
			// interceptors); we register none, so hand back an empty array of the element type.
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
				return Array.CreateInstance(type.GetGenericArguments()[0], 0);

			Type target = _registrations.TryGetValue(type, out Type? impl) ? impl : type;
			if (target.IsAbstract || target.IsInterface)
				return null;
			System.Reflection.ConstructorInfo? ctor = target.GetConstructors()
				.OrderByDescending(c => c.GetParameters().Length)
				.FirstOrDefault();
			if (ctor is null)
				return null;
			object?[] args = [.. ctor.GetParameters().Select(p => Resolve(p.ParameterType))];
			return ctor.Invoke(args);
		}
	}
}
