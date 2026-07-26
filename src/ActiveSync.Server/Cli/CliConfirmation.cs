using ActiveSync.Crypto;

namespace ActiveSync.Server.Cli;

/// <summary>
///   How a command asks the operator a question it cannot ask itself.
///   <para>
///     A forwarded command runs against a captured console built with
///     <c>InteractionSupport.No</c>, so it can never prompt: the interactive branch of a
///     destructive command only ever ran in the local-fallback path, and over <c>/cli</c> —
///     which is how `eas` normally runs inside the container — it just failed telling the
///     operator to pass <c>--yes</c>. Rather than teach the console to prompt across HTTP, the
///     command RETURNS the question; the slim client, which is a real terminal, asks it and
///     re-sends the argument list the server hands back.
///   </para>
///   <para>
///     The flow is a deliberate re-execution, not a resumed transaction: call 1 decides and
///     counts, call 2 does the work. A command must therefore RE-CHECK on the second call — the
///     operator confirmed a specific loss, not an open-ended one.
///   </para>
/// </summary>
internal static class CliConfirmation
{
	private static readonly AsyncLocal<State?> Ambient = new();

	/// <summary>The flag a resend carries — <c>--yes</c>, never a second spelling for one idea.</summary>
	internal const string ConfirmedFlag = "--yes";

	private sealed class State
	{
		public required string[] Args { get; init; }
		public ConfirmRequest? Pending { get; set; }
	}

	/// <summary>True when this command was FORWARDED, so a question can be sent back.</summary>
	internal static bool CanAsk => Ambient.Value is not null;

	/// <summary>
	///   Opens a confirmation scope for one forwarded command, remembering its argv so a command
	///   asking a question never has to re-assemble its own command line (which is exactly the
	///   quoting bug the server-supplies-ResendArgs rule exists to avoid).
	/// </summary>
	internal static void Begin(string[] args) => Ambient.Value = new State { Args = args };

	/// <summary>Closes the scope and returns the question the command parked, if any.</summary>
	internal static ConfirmRequest? End()
	{
		ConfirmRequest? pending = Ambient.Value?.Pending;
		Ambient.Value = null;
		return pending;
	}

	/// <summary>
	///   Asks the operator <paramref name="question" /> by parking it for the client, together
	///   with this command's own argv plus <c>--yes</c>. The caller must return a non-zero exit
	///   code immediately: nothing has been done yet.
	/// </summary>
	internal static void Ask(string question)
	{
		if (Ambient.Value is not { } state)
			return;
		string[] resend = state.Args.Contains(ConfirmedFlag)
			? state.Args
			: [.. state.Args, ConfirmedFlag];
		state.Pending = new ConfirmRequest(question, resend);
	}
}
