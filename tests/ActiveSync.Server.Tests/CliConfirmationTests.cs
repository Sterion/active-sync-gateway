using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The <c>ConfirmRequest</c> round-trip: <c>LocalCliResult</c> carries an optional <c>Confirm</c>
///   field so a forwarded command can ask a question and hand back <c>ResendArgs</c> instead of
///   failing outright, with a dry-run counting method both the CLI and the admin UI consume.
///   <para>
///     This fixes a real gap, not a hypothetical one: <c>LocalCliEndpoint</c> builds its captured
///     console with <c>InteractionSupport.No</c>, so a forwarded command can never prompt, and
///     <c>eas purge</c> over <c>/cli</c> ALWAYS failed with "confirm with --yes" — the interactive
///     branch only ever ran in the local-fallback path. The command now returns the question and
///     the argument list to resend; the slim client (a real terminal) asks.
///   </para>
/// </summary>
[Collection("cli")]
public sealed class CliConfirmationTests : IDisposable
{
	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliConfirmationTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-confirm-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();
		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
		SetEnv("ActiveSync__Encryption__AllowPlaintext", "true");
	}

	public void Dispose()
	{
		foreach ((string name, string? value) in _originalEnv)
			Environment.SetEnvironmentVariable(name, value);
		SqliteConnection.ClearAllPools();
		File.Delete(_dbPath);
	}

	private void SetEnv(string name, string? value)
	{
		_originalEnv.TryAdd(name, Environment.GetEnvironmentVariable(name));
		Environment.SetEnvironmentVariable(name, value);
	}

	private SqliteSyncDbContext NewContext() =>
		new(new DbContextOptionsBuilder<SqliteSyncDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

	/// <summary>Seeds a user with sync state and, optionally, irreplaceable local content.</summary>
	private async Task SeedAsync(string login, int contacts)
	{
		await using SqliteSyncDbContext db = NewContext();
		User user = new() { Login = login, UpdatedUtc = DateTime.UtcNow };
#pragma warning disable VSTHRD103
		db.Users.Add(user);
		db.SaveChanges();
		db.Devices.Add(new Device
		{
			UserId = user.UserId, DeviceId = "PHONE1", DeviceType = "T",
			CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
		});
		for (int i = 0; i < contacts; i++)
			db.LocalItems.Add(new LocalItem
			{
				UserId = user.UserId, Collection = "contacts", Uid = $"c-{i}",
				Content = "BEGIN:VCARD\r\nEND:VCARD", Version = 1, LastModifiedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();
	}

	[Fact]
	public async Task ForwardedPurge_AsksInsteadOfFailing_AndNamesWhatIsLost()
	{
		await SeedAsync("anna", contacts: 3);

		LocalCliEndpoint.CliResponse response = await LocalCliEndpoint.ExecuteAsync(
			["purge", "user", "anna"], "", CancellationToken.None);

		// It did NOT act, and it did not fall back to "pass --yes" — it asked.
		Assert.NotEqual(0, response.ExitCode);
		Assert.NotNull(response.Confirm);
		Assert.Contains("3 contacts", response.Confirm!.Question);
		Assert.Contains("nowhere else", response.Confirm.Question);
		// The SERVER supplies the resend, and it is the original argv plus --yes.
		Assert.Equal(["purge", "user", "anna", "--yes"], response.Confirm.ResendArgs);

		// Nothing was deleted by the asking call.
		await using SqliteSyncDbContext verify = NewContext();
		Assert.Equal(3, await verify.LocalItems.CountAsync());
	}

	[Fact]
	public async Task ResendingWhatTheServerAskedFor_CarriesTheDeleteOut()
	{
		await SeedAsync("anna", contacts: 3);
		LocalCliEndpoint.CliResponse asked = await LocalCliEndpoint.ExecuteAsync(
			["purge", "user", "anna"], "", CancellationToken.None);
		Assert.NotNull(asked.Confirm);

		LocalCliEndpoint.CliResponse done = await LocalCliEndpoint.ExecuteAsync(
			asked.Confirm!.ResendArgs, "", CancellationToken.None);

		Assert.Equal(0, done.ExitCode);
		Assert.Null(done.Confirm);   // the second call acts, it does not ask again
		await using SqliteSyncDbContext verify = NewContext();
		Assert.Empty(verify.LocalItems);
		Assert.Empty(verify.Devices);
	}

	[Fact]
	public async Task WithNoContentAtRisk_TheQuestionIsPlain()
	{
		// Sync state alone rebuilds on the next sync, so it does not deserve a dire warning.
		await SeedAsync("bob", contacts: 0);

		LocalCliEndpoint.CliResponse response = await LocalCliEndpoint.ExecuteAsync(
			["purge", "user", "bob"], "", CancellationToken.None);

		Assert.NotNull(response.Confirm);
		Assert.DoesNotContain("nowhere else", response.Confirm!.Question);
	}

	[Fact]
	public async Task APurgeAlreadyCarryingYes_NeverAsks()
	{
		await SeedAsync("anna", contacts: 2);

		LocalCliEndpoint.CliResponse response = await LocalCliEndpoint.ExecuteAsync(
			["purge", "user", "anna", "--yes"], "", CancellationToken.None);

		Assert.Equal(0, response.ExitCode);
		Assert.Null(response.Confirm);
		await using SqliteSyncDbContext verify = NewContext();
		Assert.Empty(verify.LocalItems);
	}

	[Fact]
	public async Task ConfirmedPurge_ReRunsTheImpactCount_AndNamesWhatItDestroys()
	{
		// CliConfirmation's own type doc states "a command must therefore RE-CHECK on the second
		// call — the operator confirmed a specific loss, not an open-ended one", and UserDeleteCommand
		// already re-counts on its --yes path. PurgeCommand's
		// --yes path skipped the whole count-and-ask block (lines 33-66) and went straight to
		// DeleteAsync, so nothing re-checked the impact or told the operator what was actually being
		// destroyed on the confirmed call — only the ASKING call ever named the content ("3 contacts").
		await SeedAsync("anna", contacts: 3);

		LocalCliEndpoint.CliResponse response = await LocalCliEndpoint.ExecuteAsync(
			["purge", "user", "anna", "--yes"], "", CancellationToken.None);

		Assert.Equal(0, response.ExitCode);
		// DescribeContent() renders "3 contacts" — distinct from the post-delete summary line's
		// "contacts: 3 row(s)" — so this only matches a pre-delete re-check message.
		Assert.Contains("3 contacts", response.Stdout);
		await using SqliteSyncDbContext verify = NewContext();
		Assert.Empty(verify.LocalItems);
	}

	[Fact]
	public async Task DevicePurge_AsksToo_AndScopesTheCountToThatDevice()
	{
		await SeedAsync("anna", contacts: 3);

		LocalCliEndpoint.CliResponse response = await LocalCliEndpoint.ExecuteAsync(
			["purge", "device", "anna", "PHONE1"], "", CancellationToken.None);

		Assert.NotNull(response.Confirm);
		// A device purge touches sync state only — the user's local items are not at risk.
		Assert.DoesNotContain("nowhere else", response.Confirm!.Question);
		Assert.Equal(["purge", "device", "anna", "PHONE1", "--yes"], response.Confirm.ResendArgs);
	}

	[Fact]
	public void TheSealedResult_CarriesTheQuestionThrough()
	{
		// The response is sealed whenever a master key exists, so the question has to survive the
		// round-trip in the sealed payload rather than only in the plaintext fields.
		byte[] key = new byte[32];
		Array.Fill(key, (byte)3);
		LocalCliEndpoint.CliResponse response = new(
			1, "", "", null, new ConfirmRequest("Delete everything?", ["purge", "user", "anna", "--yes"]));

		LocalCliEndpoint.CliResponse sealedResponse = LocalCliEndpoint.ProtectResponse(response, key);

		Assert.NotNull(sealedResponse.Sealed);
		Assert.True(LocalCliResult.TryOpen(sealedResponse.Sealed, key, out LocalCliResult? opened));
		Assert.Equal("Delete everything?", opened!.Confirm!.Question);
		Assert.Equal(["purge", "user", "anna", "--yes"], opened.Confirm.ResendArgs);
	}
}
