using System.Net;
using ActiveSync.Core.Options;
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
///   <para>Security: the ONLY gate is that the connection is loopback — that is the entire auth
///   boundary and it is essential (the public HTTP port is the one phones/ingress hit). It holds
///   because the gateway installs no forwarded-headers middleware, so <c>RemoteIpAddress</c> is the
///   true transport peer and cannot be spoofed by a header. Secrets are forwarded like any other
///   command: an in-container loopback caller already sits next to the encryption key and the state
///   database, so loopback forwarding adds no exposure. The request body is never logged
///   (<c>UseEasRequestLogging</c> records only method/path/status).</para>
/// </summary>
internal static class LocalCliEndpoint
{
	// Capturing output means swapping the process-global Console + AnsiConsole.Console, so only one
	// forwarded command may run at a time. CLI traffic is low-frequency interactive admin work, so a
	// single lock costs nothing in practice.
	private static readonly SemaphoreSlim Gate = new(1, 1);

	internal sealed record CliRequest(string[]? Args, string? Stdin);

	internal sealed record CliResponse(int ExitCode, string Stdout, string Stderr);

	internal static void Map(WebApplication app)
	{
		app.MapPost("/cli", async (HttpContext context, IOptionsMonitor<ActiveSyncOptions> options, CliRequest? request) =>
		{
			// Disabled or non-loopback: answer 404 so the endpoint is invisible to the outside.
			// The loopback check is the entire auth boundary (see the type summary).
			if (!options.CurrentValue.Cli.Enabled || !IsLoopback(context.Connection.RemoteIpAddress))
				return Results.NotFound();

			CliResponse response = await ExecuteAsync(request?.Args ?? [], request?.Stdin ?? "", context.RequestAborted);
			return Results.Json(response);
		});
	}

	/// <summary>True only for a loopback transport peer (127.0.0.0/8 or ::1); null is never allowed.</summary>
	internal static bool IsLoopback(IPAddress? peer) => peer is not null && IPAddress.IsLoopback(peer);

	/// <summary>
	///   Runs one CLI command line in-process and returns its captured output + exit code. Refuses
	///   <c>serve</c> (no nested gateway); every other verb — secret-bearing or not — is allowed.
	/// </summary>
	internal static async Task<CliResponse> ExecuteAsync(string[] args, string stdin, CancellationToken ct)
	{
		if (args.Length > 0 && string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
			return new CliResponse(1, "", "serve is not available over /cli; run it locally.\n");

		return await RunCapturedAsync(args, stdin, ct);
	}

	/// <summary>
	///   Runs the Spectre command tree with stdout/stderr/stdin redirected to per-request buffers.
	///   Commands emit through both the injected <see cref="IAnsiConsole" /> (default
	///   <see cref="AnsiConsole.Console" />) and the raw process-global <see cref="Console" />, so
	///   both are redirected under the lock and restored in <c>finally</c>.
	/// </summary>
	private static async Task<CliResponse> RunCapturedAsync(string[] args, string stdin, CancellationToken ct)
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
			// Plain-text console (no ANSI/colour/prompts): the client re-emits this verbatim. Pin a
			// wide profile so rendering never depends on the ambient terminal width (a redirected
			// System.Console can report width 0, which would wrap every line away to nothing).
			IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
			{
				Ansi = AnsiSupport.No,
				ColorSystem = ColorSystemSupport.NoColors,
				Interactive = InteractionSupport.No,
				Out = new AnsiConsoleOutput(outWriter),
			});
			captured.Profile.Width = 200;
			captured.Profile.Height = 100;
			// Static AnsiConsole.* helpers write here; commands that take an injected IAnsiConsole
			// get the same instance via CapturingRegistrar (Spectre's DEFAULT registrar caches the
			// console process-statically — the first forwarded command would otherwise capture all
			// later ones' output).
			AnsiConsole.Console = captured;

			CommandApp<BannerCommand> cli = new(new CapturingRegistrar(captured));
			cli.Configure(CliApp.Configure);
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
