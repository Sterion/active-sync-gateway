namespace ActiveSync.Server.Cli;

/// <summary>
///   L35 (item 37): publishes the warm gateway's already-built service provider to CLI commands that
///   run <em>inside</em> the <c>/cli</c> forwarding endpoint, so a forwarded database command reuses
///   the host's DI container, EF model and loaded plugins instead of building a parallel container
///   (and probing pending migrations) on every invocation — the cost that dominates a forwarded
///   command's latency.
///
///   <para>Scoped by async flow: <see cref="LocalCliEndpoint" /> publishes the host provider around
///   the command it runs, so the command's own async flow sees it. A command dispatched by the slim
///   client's LOCAL fallback (no gateway answered) runs outside that flow, sees <c>null</c>, and
///   builds its own provider through <see cref="CliServices" /> exactly as before.</para>
/// </summary>
internal static class CliHostServices
{
	private static readonly AsyncLocal<IServiceProvider?> Ambient = new();

	/// <summary>The host provider when a command runs inside <c>/cli</c>; null when it runs standalone.</summary>
	public static IServiceProvider? Current => Ambient.Value;

	/// <summary>Publishes <paramref name="host" /> for the current async flow (the forwarded command).</summary>
	public static void Enter(IServiceProvider? host) => Ambient.Value = host;
}
