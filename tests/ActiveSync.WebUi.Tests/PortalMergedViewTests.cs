using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Item 17's portal-side findings — C4, C12, C13 — all trace back to the same root: parts of the
///   portal API resolve the caller's effective account from the DATABASE ROW alone, or from a stale
///   snapshot, instead of the MERGED (config over database) view `GET backends/meta` renders from.
/// </summary>
public sealed class PortalMergedViewTests
{
	private static Dictionary<string, UserOptions> BobWithConfigLevelCalendarProvider()
	{
		return WebUiHost.Users(("bob", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new BackendRoleOverride
				{
					Provider = "caldav",
					Settings = new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com" },
				},
			},
		}));
	}

	/// <summary>
	///   C4 — `PUT backends/{roleName}` computes its self-service permission gate from
	///   `UserEditing.LoadStartingEntryAsync` (the database row alone), while `GET backends/meta`
	///   computes the same thing from the merged view. When a role's provider is set only in the
	///   user's own CONFIGURATION override (never written to the database), the GET renders fields
	///   for that provider and the PUT refuses every one of them — a form that cannot be submitted.
	/// </summary>
	[Fact]
	public async Task Meta_And_Put_AgreeOnTheProvider_WhenOnlyConfigDeclaresIt()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(BobWithConfigLevelCalendarProvider());
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		JsonElement meta = await host.ReadJsonAsync(await client.GetAsync("/user/api/backends/meta"));
		string[] names = [.. meta.GetProperty("Calendar").GetProperty("fields").EnumerateArray()
			.Select(f => f.GetProperty("name").GetString()!)];
		Assert.Contains("CalendarAttachments", names);

		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			settings = new Dictionary<string, string?> { ["CalendarAttachments"] = "Off" },
		});
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	/// <summary>
	///   C12 — the portal's `PUT backends/{roleName}` freezes config-supplied self-service values
	///   into the database row for the same reason as C2. `userName` is pre-filled from the MERGED
	///   view (`GET /user/api/me`), which reports a config-supplied backend user name too, so a save
	///   the holder never touched resubmits it verbatim — and the handler wrote it straight into the
	///   row as a permanent database override.
	/// </summary>
	[Fact]
	public async Task Put_ResubmittingTheConfigSuppliedUserName_DoesNotFreezeItAsADatabaseOverride()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(("bob", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new BackendRoleOverride
				{
					Provider = "caldav",
					UserName = "bob.dav.default",
					Settings = new Dictionary<string, string?>
					{
						["BaseUrl"] = "https://dav.example.com",
						["CalendarAttachments"] = "Auto",
					},
				},
			},
		})));
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		// Exactly what GET /user/api/me / backends/meta rendered — the holder never touched anything.
		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			userName = "bob.dav.default",
			settings = new Dictionary<string, string?> { ["CalendarAttachments"] = "Auto" },
		});
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		BackendRoleOverride? role = stored?.Backends?.GetValueOrDefault("Calendar");
		Assert.Null(role?.UserName);
		Assert.Null(role?.Settings?.GetValueOrDefault("CalendarAttachments"));
	}

	[Fact]
	public async Task Put_ActuallyChangingTheUserName_StillRecordsARealOverride()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(("bob", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new BackendRoleOverride
				{
					Provider = "caldav",
					UserName = "bob.dav.default",
					Settings = new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com" },
				},
			},
		})));
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			userName = "bob.dav.custom",
		});
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		Assert.Equal("bob.dav.custom", stored!.Backends!["Calendar"].UserName);
	}
}
