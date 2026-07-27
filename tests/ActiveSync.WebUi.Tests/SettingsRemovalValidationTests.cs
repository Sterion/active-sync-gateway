using System.Net;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   B4 — the settings DELETE endpoint deleted the database row with no validation at all, unlike a
///   write (which runs <see cref="SettingKeys.ValidateStartupImpact" />). Removing
///   <c>ActiveSync:Backends:Calendar:BaseUrl</c> while its provider assignment (caldav) remains
///   leaves a section the running gateway tolerates but the NEXT start's
///   <c>BackendConfigurationValidator</c> refuses ("BaseUrl ... must be an absolute http(s) URL");
///   this is the finding's own MailStore:Host example, reproduced against a role WebUiHost's fixed
///   registry actually serves (caldav/Calendar).
/// </summary>
public sealed class SettingsRemovalValidationTests
{
	private static Dictionary<string, UserOptions> OneAdmin() =>
		WebUiHost.Users(("root", new UserOptions { MailAddress = "root@example.com", Admin = true }));

	[Fact]
	public async Task DeletingABackendBaseUrl_ThatWouldBreakTheNextStart_IsRejected()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("root", admin: true);

		GlobalSettingStore store = new(host.Factory);
		await store.UpsertAsync("ActiveSync:Backends:Calendar:Provider", "caldav", CancellationToken.None);
		await store.UpsertAsync(
			"ActiveSync:Backends:Calendar:BaseUrl", "https://dav.example/cal/", CancellationToken.None);

		HttpResponseMessage response =
			await client.DeleteAsync("/admin/api/settings/ActiveSync:Backends:Calendar:BaseUrl");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		// Nothing must have been removed by a request that was refused.
		Assert.Equal("https://dav.example/cal/",
			await store.GetAsync("ActiveSync:Backends:Calendar:BaseUrl", CancellationToken.None));
	}

	[Fact]
	public async Task DeletingAnOrdinaryOverride_ThatLeavesAValidConfiguration_StillWorks()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("root", admin: true);

		GlobalSettingStore store = new(host.Factory);
		await store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);

		HttpResponseMessage response = await client.DeleteAsync("/admin/api/settings/ActiveSync:ReadOnly");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Null(await store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));
	}
}
