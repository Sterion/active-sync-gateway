using System.Net;
using System.Net.Http.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Switching a role's provider on the Backends page left the previous provider's stored
///   leaves in the database, and any leaf name the two providers share is silently adopted by
///   the new one. <c>BackendsEndpoints.PersistAsync</c> only iterated the request's own
///   <c>Settings</c> (plus the provider key), so a stored row for a leaf the request does not
///   mention was never deleted — the SPA shows that provider's own defaults at switch time (the
///   UI says "starts fresh"), but the server did not.
/// </summary>
public sealed class BackendProviderSwitchTests
{
	private static Dictionary<string, UserOptions> OneAdmin() =>
		WebUiHost.Users(("alice", new UserOptions { MailAddress = "alice@example.com", Admin = true }));

	[Fact]
	public async Task SwitchingProvider_DropsThePreviousProvidersStoredLeaves()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		// Assign caldav to Calendar with a couple of stored leaves.
		HttpResponseMessage first = await client.PutAsJsonAsync("/admin/api/backends/Calendar", new
		{
			provider = "caldav",
			settings = new Dictionary<string, string?>
			{
				["BaseUrl"] = "https://cal.example/",
				["Password"] = "hunter2"
			}
		});
		Assert.Equal(HttpStatusCode.OK, first.StatusCode);

		GlobalSettingStore store = new(host.Factory);
		Dictionary<string, string?> afterFirst = await store.LoadAllAsync(CancellationToken.None);
		Assert.True(afterFirst.ContainsKey("ActiveSync:Backends:Calendar:Password"));
		Assert.True(afterFirst.ContainsKey("ActiveSync:Backends:Calendar:BaseUrl"));

		// Now switch Calendar to "local" WITHOUT resubmitting either leaf — exactly what the SPA
		// does on a provider switch (it starts the new provider's form from its own defaults).
		HttpResponseMessage second = await client.PutAsJsonAsync("/admin/api/backends/Calendar", new
		{
			provider = "local",
			settings = new Dictionary<string, string?>()
		});
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);

		// The role's Provider row legitimately still exists (it now reads "local"); the two
		// leftover LEAVES from the old caldav assignment must not.
		Dictionary<string, string?> afterSwitch = await store.LoadAllAsync(CancellationToken.None);
		Assert.False(afterSwitch.ContainsKey("ActiveSync:Backends:Calendar:Password"));
		Assert.False(afterSwitch.ContainsKey("ActiveSync:Backends:Calendar:BaseUrl"));
		Assert.Equal("local", afterSwitch.GetValueOrDefault("ActiveSync:Backends:Calendar:Provider"));
	}
}
