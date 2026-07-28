using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   Outcome of a <see cref="SendDedupStore.TryClaimAsync" /> call.
/// </summary>
public enum SendClaimOutcome
{
	/// <summary>
	///   No row exists yet, or one exists but was never marked <see cref="SentCommandToken.Completed" />
	///   — either way there is no durable proof the action already happened. The caller MUST perform
	///   (or retry) the irreversible action now, then call
	///   <see cref="SendDedupStore.MarkCompletedAsync" /> immediately after it succeeds.
	/// </summary>
	PerformSend,

	/// <summary>
	///   A prior attempt for this exact (device, collection, attempt, key) already completed
	///   successfully. The caller MUST NOT repeat the action — acknowledge the recorded outcome instead.
	/// </summary>
	AlreadySent
}

/// <summary>
///   Durable send-dedup claims (<see cref="SentCommandToken" />), so an irreversible send
///   (16.x draft submit over SMTP, occurrence-CANCEL iTIP mail) is never repeated when a crash
///   lands between the send SUCCEEDING and the collection's own <c>CommitCollectionStateAsync</c>
///   — the applied-command ledger cannot cover that window because it is persisted atomically WITH
///   the SyncKey advance, so a round that never commits leaves the client resending under the SAME
///   key, which validates as Current (a fresh, empty ledger), not Replay.
///   <para>
///     A claim ALONE is not proof the send happened — only <see cref="MarkCompletedAsync" />, called
///     by the caller right after the action returns successfully, is. This two-phase shape (claim
///     durably BEFORE the action, mark-complete durably AFTER it succeeds) is what lets a resend tell
///     apart "this exact attempt already succeeded — do not repeat it" from "this exact attempt was
///     claimed but never confirmed done (still in flight, crashed, or genuinely failed) — retry it".
///     A single-phase "claim = done" design (the original shape) cannot make that distinction: a
///     transient failure (SMTP backend down, network blip) claims the send and then throws, and the
///     resend would find the claim and skip the retry entirely — reporting success to the client
///     while the mail was never sent. See the commit that introduced this file for the fuller
///     writeup of that defect and the residual window this two-phase design still leaves.
///   </para>
///   <para>
///     Like <see cref="DavItemMap" />, a claim (and its later completion) commits on its OWN
///     short-lived context (via the injected <see cref="ISyncDbContextFactory" />) rather than the
///     request-scoped one: both must be durable independently of — and before — anything the round's
///     own (much later) commit does, and must never flush a half-mutated <see cref="CollectionState" />
///     early. Falls back to the shared context only when no factory is supplied (unit tests).
///   </para>
/// </summary>
internal sealed class SendDedupStore(SyncDbContext db, ISyncDbContextFactory? factory = null)
{
	/// <summary>
	///   Durably claims one attempt at an irreversible action. Returns <see cref="SendClaimOutcome.PerformSend" />
	///   when the caller must (re)perform the action — either nothing was ever claimed, or a claim
	///   exists but was never marked complete — and <see cref="SendClaimOutcome.AlreadySent" /> only
	///   when this exact attempt already ran to completion.
	/// </summary>
	public Task<SendClaimOutcome> TryClaimAsync(
		int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		if (factory is null)
			return TryClaimAsync(db, deviceKey, collectionId, syncKeyAtClaim, key, ct);

		return ClaimOnOwnContextAsync(deviceKey, collectionId, syncKeyAtClaim, key, ct);
	}

	/// <summary>
	///   Durably marks a previously claimed attempt as completed — call this immediately after the
	///   guarded action returns successfully, before any further (best-effort) work. Never throws on
	///   "no matching row" (defensive; should not happen given the call sites always claim first).
	/// </summary>
	public Task MarkCompletedAsync(
		int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		if (factory is null)
			return MarkCompletedAsync(db, deviceKey, collectionId, syncKeyAtClaim, key, ct);

		return MarkCompletedOnOwnContextAsync(deviceKey, collectionId, syncKeyAtClaim, key, ct);
	}

	private async Task<SendClaimOutcome> ClaimOnOwnContextAsync(
		int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		await using SyncDbContext own = factory!.CreateDbContext();
		return await TryClaimAsync(own, deviceKey, collectionId, syncKeyAtClaim, key, ct).ConfigureAwait(false);
	}

	private async Task MarkCompletedOnOwnContextAsync(
		int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		await using SyncDbContext own = factory!.CreateDbContext();
		await MarkCompletedAsync(own, deviceKey, collectionId, syncKeyAtClaim, key, ct).ConfigureAwait(false);
	}

	private static async Task<SendClaimOutcome> TryClaimAsync(
		SyncDbContext ctx, int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		SentCommandToken? existing = await ctx.SentCommandTokens.AsNoTracking().FirstOrDefaultAsync(
			t => t.DeviceKey == deviceKey && t.CollectionId == collectionId &&
			     t.SyncKeyAtClaim == syncKeyAtClaim && t.Key == key, ct).ConfigureAwait(false);
		if (existing is not null)
			// Not completed: a prior attempt was claimed but never confirmed done (still running,
			// crashed, or genuinely failed) — indistinguishable from "never tried", so the caller
			// must retry rather than report a success that may never have happened.
			return existing.Completed ? SendClaimOutcome.AlreadySent : SendClaimOutcome.PerformSend;

		SentCommandToken token = new()
		{
			DeviceKey = deviceKey,
			CollectionId = collectionId,
			SyncKeyAtClaim = syncKeyAtClaim,
			Key = key,
			CreatedUtc = DateTime.UtcNow,
			Completed = false
		};
		// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
		ctx.SentCommandTokens.Add(token);
#pragma warning restore VSTHRD103
		try
		{
			await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
			return SendClaimOutcome.PerformSend;
		}
		catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent attempt claimed it first — re-read what they wrote rather than assume
			// it finished: if THEIR attempt hasn't completed either, this caller must still retry.
			SentCommandToken winner = await ctx.SentCommandTokens.AsNoTracking().FirstAsync(
				t => t.DeviceKey == deviceKey && t.CollectionId == collectionId &&
				     t.SyncKeyAtClaim == syncKeyAtClaim && t.Key == key, ct).ConfigureAwait(false);
			return winner.Completed ? SendClaimOutcome.AlreadySent : SendClaimOutcome.PerformSend;
		}
	}

	private static async Task MarkCompletedAsync(
		SyncDbContext ctx, int deviceKey, string collectionId, int syncKeyAtClaim, string key, CancellationToken ct)
	{
		await ctx.SentCommandTokens
			.Where(t => t.DeviceKey == deviceKey && t.CollectionId == collectionId &&
			            t.SyncKeyAtClaim == syncKeyAtClaim && t.Key == key)
			.ExecuteUpdateAsync(s => s.SetProperty(t => t.Completed, true), ct).ConfigureAwait(false);
	}

	/// <summary>
	///   Deletes every claim for (device, collection) whose attempt is older than
	///   <paramref name="syncKeyExclusiveUpperBound" /> — called right after a collection's commit
	///   lands, with its NEW SyncKey. Safe in every commit mode: a just-completed round's claims
	///   carried the OLD key (now covered by the generation's own applied-command ledger and its N-1
	///   replay window, so the dedup claim is no longer needed), and a Replay commit keeps the
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
