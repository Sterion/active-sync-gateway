using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.WebUi.Auth;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Three portal-side gaps below all trace back to the same root: parts of the portal API resolve
///   the caller's effective account from the DATABASE ROW alone, or from a stale snapshot, instead
///   of the MERGED (config over database) view `GET backends/meta` renders from.
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
	///   `PUT backends/{roleName}` computes its self-service permission gate from
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
	///   The portal's `PUT backends/{roleName}` freezes config-supplied self-service values into
	///   the database row for the same reason the admin Users PUT elides matching fields.
	///   `userName` is pre-filled from the MERGED
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

	/// <summary>
	///   `GET /user/api/backends/meta` reads `resolver.MergedUsers` without ever refreshing
	///   it: the handler is synchronous and never calls `EnsureFreshAsync`, unlike every other
	///   endpoint under `Api/` that touches `MergedUsers`. An admin who has just moved the caller's
	///   role to another provider (a DIFFERENT request, via the database) hands the portal a form
	///   built from the OLD provider's schema — whose fields the PUT (which resolves live) then
	///   refuses. `Auth:UsersRefreshSeconds` is forced to 0 so the fix is provable without a
	///   real-time sleep: the staleness comes from `meta` never checking at all, not from the
	///   interval not having elapsed yet.
	///   <para>
	///   The cookie-auth pipeline ALSO refreshes the resolver on its own (`SessionValidation`'s
	///   `OnValidatePrincipal` hook), but only once per 60-second `Interval`, and only when the
	///   ticket carries no `ValidatedAtClaim` yet — which is true of the very first request after
	///   sign-in. <see cref="RewarmSessionAsync" /> spends that one free refresh up front (on a
	///   snapshot that already matches "caldav", so it changes nothing observable) and captures the
	///   renewed cookie, so the interval is fresh for the rest of the test and the ONLY thing left
	///   that can explain what `meta` returns next is the endpoint's own (missing) refresh.
	///   </para>
	/// </summary>
	[Fact]
	public async Task Meta_ReflectsALiveProviderChange_WithoutATimeBasedWait()
	{
		Dictionary<string, string?> zeroRefresh = new() { ["ActiveSync:Auth:UsersRefreshSeconds"] = "0" };
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("bob", new UserOptions())), zeroRefresh);
		UserStore store = new(host.Factory);
		await store.UpsertAsync("bob", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new BackendRoleOverride
				{
					Provider = "caldav",
					Settings = new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com" },
				},
			},
		}, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("bob", admin: false);
		await RewarmSessionAsync(client);

		// An admin — a separate request entirely — switches bob's Calendar role to "local".
		await store.UpsertAsync("bob", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new BackendRoleOverride { Provider = "local" },
			},
		}, CancellationToken.None);

		JsonElement meta = await host.ReadJsonAsync(await client.GetAsync("/user/api/backends/meta"));
		Assert.Equal("local", meta.GetProperty("Calendar").GetProperty("provider").GetString());
	}

	/// <summary>
	///   Spends SessionValidation's one free per-ticket refresh on an authenticated GET that changes
	///   nothing observable, and updates <paramref name="client" />'s Cookie header to the renewed
	///   ticket the response carries (the response's Set-Cookie is otherwise never fed back into a
	///   client whose Cookie header was set manually, once, in <see cref="WebUiHost.SignInAsync" />).
	/// </summary>
	private static async Task RewarmSessionAsync(HttpClient client)
	{
		HttpResponseMessage response = await client.GetAsync("/user/api/me");
		if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
			throw new InvalidOperationException("expected the session to be renewed with a fresh cookie");
		string renewed = cookies.First(v => v.StartsWith(WebUiAuth.CookieName, StringComparison.Ordinal)).Split(';')[0];
		client.DefaultRequestHeaders.Remove("Cookie");
		client.DefaultRequestHeaders.Add("Cookie", renewed);
	}
}
