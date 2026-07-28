using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Coherence gaps in <c>SettingsEndpoints</c>.
/// </summary>
public sealed class SettingsEndpointsCoherenceTests
{
	private static Dictionary<string, UserOptions> OneAdmin() =>
		WebUiHost.Users(("root", new UserOptions { MailAddress = "root@example.com", Admin = true }));

	/// <summary>
	///   `GET settings`'s "surface stray stored keys so they can be cleared" branch could
	///   never fire: `extra` is already `db.Keys` minus every catalogue key, so
	///   `SettingKeys.Find(key)` can only return non-null for exactly the OTHER shape the
	///   `!IsBackendKey` guard excludes. A row for a key the catalogue no longer recognizes (a
	///   real example: `ActiveSync:RequireDeclaredUsers`, deleted by the restructure) was
	///   therefore invisible in the admin UI and clearable only by guessing the DELETE URL.
	/// </summary>
	[Fact]
	public async Task AStrayDatabaseKey_NotInTheCatalogue_IsSurfacedAsClearable()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("root", admin: true);

		GlobalSettingStore store = new(host.Factory);
		await store.UpsertAsync("ActiveSync:RequireDeclaredUsers", "true", CancellationToken.None);

		JsonElement settings = await host.ReadJsonAsync(await client.GetAsync("/admin/api/settings"));
		// A stored row's key is normalized to lowercase on write — the point here is that the row is
		// surfaced AT ALL (it used to be dropped entirely), not its display casing.
		JsonElement stray = settings.EnumerateArray()
			.Single(s => string.Equals(
				s.GetProperty("key").GetString(), "ActiveSync:RequireDeclaredUsers",
				StringComparison.OrdinalIgnoreCase));

		Assert.Equal("db", stray.GetProperty("source").GetString());
		Assert.Equal("true", stray.GetProperty("value").GetString());
	}

	/// <summary>
	///   Resetting a setting badges its source as "default" even when the config file still
	///   supplies a value for the same key. The DELETE response must report the recomputed
	///   effective source (it already recomputes tier), so the JS badge does not lie until reload.
	/// </summary>
	[Fact]
	public async Task DeletingADbOverride_ThatLeavesAConfigFileValue_ReportsConfigNotDefault()
	{
		Dictionary<string, string?> configFile = new() { ["ActiveSync:ReadOnly"] = "true" };
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin(), configFile);
		using HttpClient client = await host.SignInAsync("root", admin: true);

		GlobalSettingStore store = new(host.Factory);
		await store.UpsertAsync("ActiveSync:ReadOnly", "false", CancellationToken.None);

		HttpResponseMessage response = await client.DeleteAsync("/admin/api/settings/ActiveSync:ReadOnly");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		JsonElement body = await host.ReadJsonAsync(response);
		Assert.Equal("config", body.GetProperty("source").GetString());
	}
}
