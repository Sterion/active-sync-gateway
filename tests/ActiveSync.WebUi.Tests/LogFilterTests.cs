using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C15 — the free-text log filter. Two halves, and only one of them reproduced.
///
///   The wildcard tests below are COVERAGE, NOT REPRODUCERS. The finding reads the filter as
///   passing user input into an unescaped LIKE pattern; it does not. EF translates
///   <c>string.Contains</c> to a literal substring search — <c>instr("Message", @text) &gt; 0</c>
///   on Sqlite (checked against the generated SQL), <c>strpos</c> on Npgsql — so '%' and '_'
///   have never been wildcards here. They are kept to pin that: the "fix" of switching to
///   <c>EF.Functions.Like</c> would introduce exactly the defect the finding describes, and
///   these go red if someone does it.
///
///   The tail-mode time floor DID reproduce: the branch had none, so a poll repeating every 2 s
///   scanned from whatever row the cursor named, however old.
/// </summary>
public sealed class LogFilterTests
{
	private static async Task SeedAsync(ISyncDbContextFactory factory, params (string Message, int AgeMinutes)[] rows)
	{
		await using SyncDbContext db = factory.CreateDbContext();
		foreach ((string message, int age) in rows)
			// DbSet.Add is synchronous and local (no I/O).
#pragma warning disable VSTHRD103
			db.LogEntries.Add(new LogEntry
			{
				TimestampUtc = DateTime.UtcNow.AddMinutes(-age),
				Level = "Information",
				Message = message
			});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(CancellationToken.None);
	}

	private static async Task<WebUiHost> AdminHostAsync()
	{
		return await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new AccountOptions { Admin = true })));
	}

	private static string[] Messages(JsonElement body)
	{
		return [.. body.GetProperty("entries").EnumerateArray()
			.Select(entry => entry.GetProperty("message").GetString()!)];
	}

	[Fact]
	public async Task WildcardCharacters_AreMatchedLiterally()
	{
		await using WebUiHost host = await AdminHostAsync();
		await SeedAsync(host.Factory, ("plain message", 1), ("50% off", 1), ("a_b", 1));
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement percent = await host.ReadJsonAsync(
			await client.GetAsync("/admin/api/logs?text=%25"));
		Assert.Equal(["50% off"], Messages(percent));

		// "_" is LIKE's single-character wildcard; typed into the filter box it means "_".
		JsonElement underscore = await host.ReadJsonAsync(
			await client.GetAsync("/admin/api/logs?text=a_b"));
		Assert.Equal(["a_b"], Messages(underscore));
	}

	[Fact]
	public async Task OrdinaryText_StillMatchesAsASubstring()
	{
		await using WebUiHost host = await AdminHostAsync();
		await SeedAsync(host.Factory, ("connection refused", 1), ("all good", 1));
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement body = await host.ReadJsonAsync(
			await client.GetAsync("/admin/api/logs?text=refus"));
		Assert.Equal(["connection refused"], Messages(body));
	}

	[Fact]
	public async Task TailMode_AppliesTheSameTimeFloorAsHistory()
	{
		await using WebUiHost host = await AdminHostAsync();
		await SeedAsync(host.Factory, ("ancient", 5000), ("recent", 1));
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		// after=0 is what the live tail sends on its first poll; without a floor it walks the
		// whole table from Id 1 regardless of the window the operator selected.
		JsonElement body = await host.ReadJsonAsync(
			await client.GetAsync("/admin/api/logs?after=0&sinceMinutes=60"));
		Assert.Equal(["recent"], Messages(body));
	}
}

