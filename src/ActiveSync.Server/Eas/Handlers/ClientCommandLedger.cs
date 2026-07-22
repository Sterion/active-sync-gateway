using ActiveSync.Core.State;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>
///   The client-command replay ledger for one collection round. Holds the applied-Add and
///   applied-Change maps of the PREVIOUS generation (consulted when a client re-sends commands
///   whose response it never saw) and accumulates the maps for the CURRENT generation (persisted
///   with the new SyncKey). Collapsing the four parallel dictionaries into one object is what
///   lets <see cref="SyncHandler.ApplyClientCommandAsync" /> take a single ledger, and makes the
///   "replay first, then record" ordering enforceable in one place.
/// </summary>
internal sealed class ClientCommandLedger
{
	private readonly Dictionary<string, AppliedClientAdd> _replayedAdds;
	private readonly Dictionary<string, AppliedClientChange> _replayedChanges;

	private ClientCommandLedger(
		Dictionary<string, AppliedClientAdd> replayedAdds,
		Dictionary<string, AppliedClientChange> replayedChanges)
	{
		_replayedAdds = replayedAdds;
		_replayedChanges = replayedChanges;
	}

	/// <summary>The applied-Add map accumulated this round (persisted with the new SyncKey).</summary>
	public Dictionary<string, AppliedClientAdd> AppliedAdds { get; } = new(StringComparer.Ordinal);

	/// <summary>The applied-Change map accumulated this round (persisted with the new SyncKey).</summary>
	public Dictionary<string, AppliedClientChange> AppliedChanges { get; } = new(StringComparer.Ordinal);

	/// <summary>A ledger seeded from the rolled-back generation's applied maps (a Replay round).</summary>
	public static ClientCommandLedger ForReplay(CollectionState state)
	{
		return new ClientCommandLedger(
			SyncStateService.ReadAppliedAdds(state), SyncStateService.ReadAppliedChanges(state));
	}

	/// <summary>An empty ledger (a Current/Initial round, where the client is not retrying).</summary>
	public static ClientCommandLedger Empty()
	{
		return new ClientCommandLedger([], []);
	}

	/// <summary>The recorded outcome of a client Add with this ClientId in the replayed generation.</summary>
	public bool TryReplayAdd(string clientId, out AppliedClientAdd? replayed)
	{
		return _replayedAdds.TryGetValue(clientId, out replayed);
	}

	/// <summary>The recorded outcome of a client Change (or occurrence cancel) keyed as replayed.</summary>
	public bool TryReplayChange(string key, out AppliedClientChange? replayed)
	{
		return _replayedChanges.TryGetValue(key, out replayed);
	}

	public void RecordAdd(string clientId, AppliedClientAdd add)
	{
		AppliedAdds[clientId] = add;
	}

	public void RecordChange(string key, AppliedClientChange change)
	{
		AppliedChanges[key] = change;
	}
}
