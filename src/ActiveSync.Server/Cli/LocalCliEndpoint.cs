using System.Diagnostics;
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

	/// <summary>
	///   Remembers the nonces of envelopes already executed, so one envelope runs exactly once. The
	///   timestamp window bounds a captured envelope in time; this bounds it in count, which is what
	///   matters for a destructive verb (<c>purge user --yes</c> replayed is a second purge).
	///
	///   <para>Only a caller that already proved key possession can add an entry, and every entry is
	///   pruned once it falls outside the window it was admitted under, so the table stays the size
	///   of one window's worth of legitimate admin traffic.</para>
	/// </summary>
	internal sealed class ReplayCache(long retentionMs)
	{
		private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
		private readonly Lock _gate = new();

		/// <summary>
		///   Claims <paramref name="nonce" /> for this execution: true the first time it is seen,
		///   false for every repeat while the nonce is still within the replay window.
		/// </summary>
		public bool TryClaim(string nonce, long nowUnixMs)
		{
			lock (_gate)
			{
				// An envelope older than the window is rejected before it gets here, so anything
				// that has aged out can never be presented again and its entry is dead weight.
				foreach (string expired in _seen.Where(e => nowUnixMs - e.Value > retentionMs).Select(e => e.Key).ToList())
					_seen.Remove(expired);

				if (_seen.ContainsKey(nonce))
					return false;
				_seen[nonce] = nowUnixMs;
				return true;
			}
		}
	}

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

		ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LocalCliEndpoint));
		ReplayCache replay = new(AuthWindowMs);

		app.MapPost("/cli", async (HttpContext context, IOptionsMonitor<ActiveSyncOptions> options, CliRequest? request) =>
		{
			// Disabled or non-loopback: 404 so the endpoint is invisible. Loopback is a cheap
			// pre-filter; the real auth is proof of the master key (see TryAuthorize).
			if (!options.CurrentValue.Cli.Enabled)
				return Results.NotFound();
			if (!IsLoopback(context.Connection.RemoteIpAddress))
			{
				AuditRefusal(logger, context.Connection.RemoteIpAddress, "the peer is not on the loopback interface");
				return Results.NotFound();
			}
			if (!TryAuthorize(request, key, allowPlaintext, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				    replay, out string[] args, out string stdin))
			{
				AuditRefusal(logger, context.Connection.RemoteIpAddress,
					key is null
						? "no master key is configured and AllowPlaintext is not set"
						: "no fresh, unused request sealed with the master key was presented");
				return Results.NotFound();
			}

			long startedAt = Stopwatch.GetTimestamp();
			CliResponse response = await ExecuteAsync(args, stdin, context.RequestAborted,
				request?.Color ?? false, request?.Width ?? 0);
			AuditCommand(logger, args, response.ExitCode,
				(long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, key is not null);
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
		CliRequest? request, byte[]? key, bool allowPlaintext, long nowUnixMs, ReplayCache replay,
		out string[] args, out string stdin)
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
			|| envelope is null
			|| !replay.TryClaim(envelope.Nonce, nowUnixMs))
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
	///   Tokens whose FOLLOWING argument is a secret value. Matched as a substring, case-insensitively,
	///   against both option names (<c>--password</c>) and configuration/field paths
	///   (<c>ActiveSync:Encryption:Key</c>, <c>Backends:MailStore:Settings:Password</c>), so the
	///   audit line keeps the verb and the target while dropping the value. Over-redaction is the
	///   safe direction here — an audit trail has to be safe to retain.
	/// </summary>
	private static readonly string[] SecretTokens = ["pass", "secret", "key", "token", "credential"];

	/// <summary>
	///   Renders an argv for the audit log: verbs, targets and option names in full, secret values
	///   replaced by <c>***</c>. Stdin is never rendered — it is where every secret is supposed to
	///   travel in the first place.
	/// </summary>
	internal static string DescribeCommand(string[] args)
	{
		if (args.Length == 0)
			return "(no arguments)";

		List<string> parts = new(args.Length);
		bool redactNext = false;
		for (int index = 0; index < args.Length; index++)
		{
			string argument = args[index];
			// A following option (-x/--x) is never the redacted value — it means the secret-named
			// token was the last positional, e.g. `config get ActiveSync:Encryption:Key`.
			if (redactNext && !argument.StartsWith('-'))
			{
				parts.Add("***");
				redactNext = false;
				continue;
			}

			redactNext = false;
			int equals = argument.IndexOf('=');
			if (equals > 0 && IsSecretToken(argument[..equals]))
			{
				parts.Add($"{argument[..equals]}=***");
				continue;
			}

			parts.Add(argument);
			// The first two positionals are the command path (`device password`, `user secret`) —
			// what follows them is a login or a device id, and redacting THAT would gut the audit
			// trail. Only an option or a later positional (a field/config path) names a value.
			redactNext = IsSecretToken(argument) && (index > 1 || argument.StartsWith('-'));
		}

		return string.Join(' ', parts);
	}

	private static bool IsSecretToken(string token) =>
		SecretTokens.Any(secret => token.Contains(secret, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	///   The audit record for one forwarded command. Every <c>/cli</c> call is an administrative
	///   action — account deletion, device-password disclosure, credential changes — so each one
	///   leaves a line naming what ran, how it authenticated and what it returned.
	/// </summary>
	internal static void AuditCommand(ILogger logger, string[] args, int exitCode, long elapsedMs, bool sealedAuth) =>
		logger.LogInformation(
			"/cli: {Command} — exit {ExitCode} in {ElapsedMs}ms ({Auth} auth)",
			DescribeCommand(args), exitCode, elapsedMs, sealedAuth ? "sealed" : "plaintext");

	/// <summary>
	///   The audit record for a refused call. Deliberately omits the body: a caller that failed
	///   authentication is untrusted input, and its argv has no place in the log.
	/// </summary>
	internal static void AuditRefusal(ILogger logger, IPAddress? peer, string reason) =>
		logger.LogWarning("/cli: refused a request from {Peer} — {Reason}.", peer?.ToString() ?? "(unknown)", reason);

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
	///
	///   <para>The <see cref="Console" /> swap is process-global, so it is routed by async flow
	///   (<see cref="ScopedConsoleWriter" />) rather than swallowing everything: only writes made
	///   inside this command's flow land in its buffer. Gateway log output — written from threads
	///   that predate the command — keeps reaching the real console instead of being captured into
	///   an admin's stdout and lost from the container log.</para>
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
		ScopedConsoleWriter outRouter = new(originalOut);
		ScopedConsoleWriter errorRouter = new(originalError);
		try
		{
			Console.SetOut(outRouter);
			Console.SetError(errorRouter);
			Console.SetIn(new StringReader(stdin));
			// A command may fan out to its own tasks (which DO inherit the flow), so the buffers are
			// written through synchronized wrappers; their contents are read back after the run.
			outRouter.Capture(TextWriter.Synchronized(outWriter));
			errorRouter.Capture(TextWriter.Synchronized(errorWriter));
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
	///   A <see cref="Console" /> stand-in that routes each write by ASYNC FLOW: writes made inside a
	///   forwarded command (which has called <see cref="Capture" />) go to that command's buffer;
	///   every other write — the logging providers' threads, other requests in flight — goes to the
	///   real console it replaced. Without this, redirecting the process-global console for the
	///   duration of a command captured the whole gateway's log output into one admin's stdout and
	///   deleted it from the container log.
	/// </summary>
	private sealed class ScopedConsoleWriter(TextWriter fallback) : TextWriter
	{
		private readonly AsyncLocal<TextWriter?> _scoped = new();

		public override System.Text.Encoding Encoding => fallback.Encoding;

		private TextWriter Target => _scoped.Value ?? fallback;

		/// <summary>Routes writes from the CURRENT async flow (and the flows it starts) to <paramref name="buffer" />.</summary>
		public void Capture(TextWriter buffer) => _scoped.Value = buffer;

		public override void Write(char value) => Target.Write(value);

		public override void Write(string? value) => Target.Write(value);

		public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);

		public override void WriteLine(string? value) => Target.WriteLine(value);

		public override void Flush() => Target.Flush();
	}

	/// <summary>
	///   Minimal Spectre DI bridge: resolves <see cref="IAnsiConsole" /> to the per-request captured
	///   console and constructs everything else by its longest constructor (recursively resolving
	///   parameters). Used instead of Spectre's default registrar, which caches the console in a
	///   process-static — fatal when successive forwarded commands each need their own buffer.
	/// </summary>
	internal sealed class CapturingRegistrar(IAnsiConsole console) : ITypeRegistrar, ITypeResolver
	{
		private readonly Dictionary<Type, object> _instances = new() { [typeof(IAnsiConsole)] = console };
		private readonly Dictionary<Type, Type> _registrations = [];
		private readonly Dictionary<Type, Func<object?>> _factories = [];

		public void Register(Type service, Type implementation) => _registrations[service] = implementation;

		public void RegisterInstance(Type service, object implementation) => _instances[service] = implementation;

		public void RegisterLazy(Type service, Func<object> factory) => _factories[service] = factory;

		public ITypeResolver Build() => this;

		public object? Resolve(Type? type) => Resolve(type, []);

		/// <summary>
		///   <paramref name="resolving" /> is the chain of types currently under construction. A
		///   parameter that reappears in it is a dependency cycle, and the recursion below has no
		///   other way out: a <see cref="StackOverflowException" /> cannot be caught, so one looping
		///   command graph would take the whole gateway process down. Hand back null and let the
		///   constructor decide what a missing dependency means.
		/// </summary>
		private object? Resolve(Type? type, HashSet<Type> resolving)
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
			if (!resolving.Add(target))
				return null;
			try
			{
				object?[] args = [.. ctor.GetParameters().Select(p => Resolve(p.ParameterType, resolving))];
				return ctor.Invoke(args);
			}
			finally
			{
				resolving.Remove(target);
			}
		}
	}
}
