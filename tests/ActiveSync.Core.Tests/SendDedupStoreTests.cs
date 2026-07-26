using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   F2: the send-dedup primitive (<see cref="SyncStateService.TryClaimSendAsync" /> /
///   <see cref="SyncStateService.MarkSendCompletedAsync" /> / <see cref="SentCommandToken" />) that
///   <c>SyncHandler.ApplyClientCommandAsync</c> uses to durably claim an irreversible action BEFORE
///   it happens, independently of the round's own SyncKey/ledger commit. Coverage of the primitive's
///   own contract — the handler-level crash-then-resend scenarios (both the successful-send case and
///   the failed-send-then-retry case the two-phase claim/complete design exists to support) are proven
///   red-first in <c>DraftSubmitIdempotencyTests</c>; there is no "unmodified" baseline for this
///   primitive itself to reproduce against, so these are N/A for red-first.
/// </summary>
public sealed class SendDedupStoreTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SyncDbContext _db;
	private readonly SyncStateService _service;

	public SendDedupStoreTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection)
			.Options;
		_db = new SqliteSyncDbContext(options);
		_db.Database.EnsureCreated();
		_service = new SyncStateService(_db);
	}

	public void Dispose()
	{
		_db.Dispose();
		_connection.Dispose();
	}

	/// <summary>Get-or-create the identity row for a login (rows are keyed by UserId now).</summary>
	private async Task<int> UserAsync(string login)
	{
		string normalized = login.ToLowerInvariant();
		User? user = await _db.Users.FirstOrDefaultAsync(u => u.Login == normalized);
		if (user is null)
		{
			user = new User { Login = normalized, UpdatedUtc = DateTime.UtcNow };
#pragma warning disable VSTHRD103
			_db.Users.Add(user);
#pragma warning restore VSTHRD103
			await _db.SaveChangesAsync();
		}

		return user.UserId;
	}

	[Fact]
	public async Task TryClaimSendAsync_SecondClaimAfterCompletion_ReturnsAlreadySent()
	{
		Device device = await _service.GetOrCreateDeviceAsync(await UserAsync("u@x"), "DEV1", "Phone", CancellationToken.None);

		SendClaimOutcome first = await _service.TryClaimSendAsync(device, "5", 1, "add:c1", CancellationToken.None);
		Assert.Equal(SendClaimOutcome.PerformSend, first);
		await _service.MarkSendCompletedAsync(device, "5", 1, "add:c1", CancellationToken.None);

		SendClaimOutcome second = await _service.TryClaimSendAsync(device, "5", 1, "add:c1", CancellationToken.None);
		// The exact protection F2 needs: a resend of an attempt that ALREADY SUCCEEDED is a no-op.
		Assert.Equal(SendClaimOutcome.AlreadySent, second);
	}

	[Fact]
	public async Task TryClaimSendAsync_SecondClaimWithoutCompletion_StillReturnsPerformSend()
	{
		// The defect this repair closes: a claim by itself is not proof the guarded action ever
		// happened — only MarkSendCompletedAsync durably records that. Without it (the action never
		// finished — crashed, or failed with an ordinary transient error), a resend must retry, not
		// silently report success.
		Device device = await _service.GetOrCreateDeviceAsync(await UserAsync("u@x2"), "DEV1", "Phone", CancellationToken.None);

		SendClaimOutcome first = await _service.TryClaimSendAsync(device, "5", 1, "add:c1", CancellationToken.None);
		Assert.Equal(SendClaimOutcome.PerformSend, first);

		SendClaimOutcome second = await _service.TryClaimSendAsync(device, "5", 1, "add:c1", CancellationToken.None);
		Assert.Equal(SendClaimOutcome.PerformSend, second);
	}

	[Fact]
	public async Task TryClaimSendAsync_SameKey_DifferentSyncKey_IsANewAttempt()
	{
		// A LEGITIMATE later edit reusing the same ServerId (e.g. a second, unrelated draft edit on
		// the same item) must not be mistaken for a crash-retry of an earlier, unrelated attempt —
		// the two are scoped to different generations.
		Device device = await _service.GetOrCreateDeviceAsync(await UserAsync("u@y"), "DEV1", "Phone", CancellationToken.None);

		SendClaimOutcome atGenerationOne = await _service.TryClaimSendAsync(device, "5", 1, "change:5:42", CancellationToken.None);
		SendClaimOutcome atGenerationTwo = await _service.TryClaimSendAsync(device, "5", 2, "change:5:42", CancellationToken.None);

		Assert.Equal(SendClaimOutcome.PerformSend, atGenerationOne);
		Assert.Equal(SendClaimOutcome.PerformSend, atGenerationTwo);
	}

	[Fact]
	public async Task CommitCollectionStateAsync_PrunesClaimsOlderThanTheNewGeneration()
	{
		Device device = await _service.GetOrCreateDeviceAsync(await UserAsync("u@z"), "DEV1", "Phone", CancellationToken.None);

		(SyncKeyValidation validation, CollectionState? state) =
			await _service.ValidateSyncKeyAsync(device, "5", "0", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Initial, validation);
		int key1 = await _service.CommitCollectionStateAsync(
			state!, [], 0, SyncKeyValidation.Initial, CancellationToken.None);
		Assert.Equal(1, key1);

		// The round processing key 1 claims a send, then (in this test) succeeds and commits —
		// unlike the crash scenario, where the round never reaches this commit.
		SendClaimOutcome claimed = await _service.TryClaimSendAsync(device, "5", key1, "add:c1", CancellationToken.None);
		Assert.Equal(SendClaimOutcome.PerformSend, claimed);

		(validation, state) = await _service.ValidateSyncKeyAsync(device, "5", "1", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Current, validation);
		int key2 = await _service.CommitCollectionStateAsync(
			state!, [], 0, SyncKeyValidation.Current, CancellationToken.None);
		Assert.Equal(2, key2);

		// The claim tagged with the now-superseded generation (key1) must be gone — it can never be
		// matched again (a future attempt would carry the CURRENT generation's key), so keeping it
		// would only be a leak, never a correctness aid.
		int remaining = await _db.SentCommandTokens.CountAsync(
			t => t.DeviceKey == device.Id && t.CollectionId == "5" && t.SyncKeyAtClaim == key1);
		Assert.Equal(0, remaining);
	}

	[Fact]
	public async Task CommitCollectionStateAsync_Replay_DoesNotPruneClaimsForTheUnchangedKey()
	{
		// A Replay commit does NOT advance SyncKey (the client is retrying the one-behind key), so
		// a claim tagged with that SAME still-current key must survive — a second genuine replay of
		// the identical resend must still find it (and, once the original attempt completed, must
		// still report it as already sent).
		Device device = await _service.GetOrCreateDeviceAsync(await UserAsync("u@w"), "DEV1", "Phone", CancellationToken.None);

		await using (SqliteSyncDbContext seed = StateTestSupport.NewContext(_connection))
		{
			CollectionState c = new() { DeviceKey = device.Id, CollectionId = "5", SyncKey = 2 };
			SyncStateService.WriteSnapshot(c, new Dictionary<string, string>());
			SyncStateService.WritePreviousSnapshot(c, new Dictionary<string, string>());
			await seed.CollectionStates.AddAsync(c, CancellationToken.None);
			await seed.SaveChangesAsync(CancellationToken.None);
		}

		SendClaimOutcome claimed = await _service.TryClaimSendAsync(device, "5", 2, "add:c1", CancellationToken.None);
		Assert.Equal(SendClaimOutcome.PerformSend, claimed);
		await _service.MarkSendCompletedAsync(device, "5", 2, "add:c1", CancellationToken.None);

		(SyncKeyValidation validation, CollectionState? state) =
			await _service.ValidateSyncKeyAsync(device, "5", "1", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Replay, validation);
		int keyAfterReplay = await _service.CommitCollectionStateAsync(
			state!, new Dictionary<string, string>(), 0, SyncKeyValidation.Replay, CancellationToken.None);
		Assert.Equal(2, keyAfterReplay); // Replay does not advance the key

		SendClaimOutcome secondReplayClaimAttempt =
			await _service.TryClaimSendAsync(device, "5", 2, "add:c1", CancellationToken.None);
		Assert.Equal(SendClaimOutcome.AlreadySent, secondReplayClaimAttempt); // still claimed AND completed — must not repeat
	}
}
