using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   F2: durable send-dedup claims (<see cref="SentCommandToken" />), so an irreversible send
///   (16.x draft submit over SMTP, occurrence-CANCEL iTIP mail) is never repeated when a crash
///   lands between the send and the collection's own <c>CommitCollectionStateAsync</c> — the
///   applied-command ledger cannot cover that window because it is persisted atomically WITH the
///   SyncKey advance, so a round that never commits leaves the client resending under the SAME
///   key, which validates as Current (a fresh, empty ledger), not Replay.
///   <para>
///     Like <see cref="DavItemMap" />, a claim commits on its OWN short-lived context (via the
///     injected <see cref="ISyncDbContextFactory" />) rather than the request-scoped one: it must
///     be durable independently of — and before — anything the round's own (much later) commit
///     does, and must never flush a half-mutated <see cref="CollectionState" /> early. Falls back
///     to the shared context only when no factory is supplied (unit tests).
///   </para>
/// </summary>
internal sealed class SendDedupStore(SyncDbContext db, ISyncDbContextFactory? factory = null)
{
	/// <summary>
	///   Durably claims one send. Returns <c>true</c> the first time (the caller must perform the
	///   send) and <c>false</c> when this exact (device, collection, attempt, key) was already
	///   claimed — meaning the send already happened (or crashed mid-flight after claiming but
	///   before the caller's own next step); the caller must NOT repeat it.
	/// </summary>
	public Task<bool> TryClaimAsync(
		int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		if (factory is null)
			return TryClaimAsync(db, deviceKey, collectionId, syncKeyAtClaim, key, ct);

		return ClaimOnOwnContextAsync(deviceKey, collectionId, syncKeyAtClaim, key, ct);
	}

	private async Task<bool> ClaimOnOwnContextAsync(
		int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		await using SyncDbContext own = factory!.CreateDbContext();
		return await TryClaimAsync(own, deviceKey, collectionId, syncKeyAtClaim, key, ct).ConfigureAwait(false);
	}

	private static async Task<bool> TryClaimAsync(
		SyncDbContext ctx, int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		bool exists = await ctx.SentCommandTokens.AsNoTracking().AnyAsync(
			t => t.DeviceKey == deviceKey && t.CollectionId == collectionId &&
			     t.SyncKeyAtClaim == syncKeyAtClaim && t.Key == key, ct).ConfigureAwait(false);
		if (exists)
			return false;

		SentCommandToken token = new()
		{
			DeviceKey = deviceKey,
			CollectionId = collectionId,
			SyncKeyAtClaim = syncKeyAtClaim,
			Key = key,
			CreatedUtc = DateTime.UtcNow
		};
		// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
		ctx.SentCommandTokens.Add(token);
#pragma warning restore VSTHRD103
		try
		{
			await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
			return true;
		}
		catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent attempt claimed it first — the send already happened (or is in flight)
			// there. Any other failure keeps its diagnostic (A9-style).
			return false;
		}
	}

	/// <summary>
	///   Deletes every claim for (device, collection) whose attempt is older than
	///   <paramref name="syncKeyExclusiveUpperBound" /> — called right after a collection's commit
	///   lands, with its NEW SyncKey. Safe in every commit mode: a just-completed round's claims
	///   carried the OLD key (now covered by the generation's own applied-command ledger and its
	///   N-1 replay window, so the dedup claim is no longer needed), and a Replay commit keeps the
	///   SAME key, so claims still tagged with it are deliberately preserved.
	/// </summary>
	public async Task PruneAsync(int deviceKey, string collectionId, int syncKeyExclusiveUpperBound, CancellationToken ct)
	{
		await db.SentCommandTokens
			.Where(t => t.DeviceKey == deviceKey && t.CollectionId == collectionId &&
			            t.SyncKeyAtClaim < syncKeyExclusiveUpperBound)
			.ExecuteDeleteAsync(ct).ConfigureAwait(false);
	}
}
