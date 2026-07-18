namespace ActiveSync.Server.Eas;

/// <summary>
///   One EAS command (Sync, Ping, FolderSync, …). Registered in DI and dispatched
///   by <see cref="EasEndpoint" /> on the request's Cmd.
/// </summary>
public interface IEasCommandHandler
{
	string Command { get; }
	Task HandleAsync(EasContext context, CancellationToken ct);
}
