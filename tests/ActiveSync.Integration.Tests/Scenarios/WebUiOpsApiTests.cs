using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The admin ops API (devices / logs / state / summary): device actions with typed
///   confirmations, the Id-cursor log tail, and the live-state snapshots.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class WebUiOpsApiTests(GatewayFixture gateway) : IAsyncLifetime
{
	private const string AdminUser = "opsadmin";
	private const string Password = "ops-pa55!";

	private WebApplicationFactory<Program> _factory = null!;
	private HttpClient _client = null!;

	public async Task InitializeAsync()
	{
		_factory = gateway.CreateIsolatedFactory(new Dictionary<string, string?>
		{
			["ActiveSync:WebUi:Admin:Enabled"] = "true",
			[$"ActiveSync:Users:{AdminUser}:Password"] = GatewayPasswordHasher.Hash(Password),
			[$"ActiveSync:Users:{AdminUser}:Admin"] = "true"
		});
		_client = _factory.CreateDefaultClient(new CookieContainerHandler());
		HttpResponseMessage login = await PostAsync("/admin/api/login", new { username = AdminUser, password = Password });
		Assert.Equal(HttpStatusCode.OK, login.StatusCode);
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	private async Task<HttpResponseMessage> PostAsync(string path, object body)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, path);
		request.Headers.Add("X-EAS-WebUi", "1");
		request.Content = JsonContent.Create(body);
		return await _client.SendAsync(request);
	}

	private async Task SeedDeviceAsync(string user, string deviceId)
	{
		using IServiceScope scope = _factory.Services.CreateScope();
		SyncDbContext db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
		// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only for async value
		// generators, which this project doesn't use.
#pragma warning disable VSTHRD103
		db.Devices.Add(new Device
		{
			UserName = user,
			DeviceId = deviceId,
			DeviceType = "TestPhone",
			LastProtocolVersion = "16.1",
			CreatedUtc = DateTime.UtcNow,
			LastSeenUtc = DateTime.UtcNow
		});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();
	}

	[Fact]
	public async Task Devices_BlockWipePurge_WithTypedConfirmations()
	{
		await SeedDeviceAsync("deviceuser", "DEVOPS01");

		// List shows the seeded partnership, unblocked.
		JsonElement devices = await _client.GetFromJsonAsync<JsonElement>("/admin/api/devices?user=deviceuser");
		JsonElement device = Assert.Single(devices.EnumerateArray());
		Assert.False(device.GetProperty("blocked").GetBoolean());

		// Block (device-level) -> listed as blocked; unblock reverts.
		Assert.Equal(HttpStatusCode.OK,
			(await PostAsync("/admin/api/devices/block", new { user = "deviceuser", deviceId = "DEVOPS01" })).StatusCode);
		devices = await _client.GetFromJsonAsync<JsonElement>("/admin/api/devices?user=deviceuser");
		Assert.True(devices.EnumerateArray().Single().GetProperty("blocked").GetBoolean());
		Assert.Equal(HttpStatusCode.OK,
			(await PostAsync("/admin/api/devices/unblock", new { user = "deviceuser", deviceId = "DEVOPS01" })).StatusCode);

		// Wipe demands the exact device id echoed back.
		Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/admin/api/devices/wipe",
			new { user = "deviceuser", deviceId = "DEVOPS01", cancel = false, confirm = "wrong" })).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await PostAsync("/admin/api/devices/wipe",
			new { user = "deviceuser", deviceId = "DEVOPS01", cancel = false, confirm = "DEVOPS01" })).StatusCode);
		devices = await _client.GetFromJsonAsync<JsonElement>("/admin/api/devices?user=deviceuser");
		Assert.True(devices.EnumerateArray().Single().GetProperty("pendingAccountWipe").GetBoolean());
		Assert.Equal(HttpStatusCode.OK, (await PostAsync("/admin/api/devices/wipe",
			new { user = "deviceuser", deviceId = "DEVOPS01", cancel = true })).StatusCode);

		// Purge demands the echo too, then removes the partnership.
		Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/admin/api/devices/purge",
			new { user = "deviceuser", deviceId = "DEVOPS01", confirm = "nope" })).StatusCode);
		HttpResponseMessage purge = await PostAsync("/admin/api/devices/purge",
			new { user = "deviceuser", deviceId = "DEVOPS01", confirm = "DEVOPS01" });
		Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
		devices = await _client.GetFromJsonAsync<JsonElement>("/admin/api/devices?user=deviceuser");
		Assert.Empty(devices.EnumerateArray());
	}

	[Fact]
	public async Task Logs_HistoryAndTailCursor()
	{
		// The gateway has logged plenty by now (startup banner etc.).
		JsonElement history = await _client.GetFromJsonAsync<JsonElement>(
			"/admin/api/logs?sinceMinutes=60&limit=50");
		long lastId = history.GetProperty("lastId").GetInt64();
		Assert.True(lastId > 0);
		Assert.True(history.GetProperty("entries").GetArrayLength() > 0);

		// Tail: strictly increasing ids after the cursor, chronological order.
		JsonElement tail = await _client.GetFromJsonAsync<JsonElement>(
			$"/admin/api/logs?after={lastId}");
		long previous = lastId;
		foreach (JsonElement entry in tail.GetProperty("entries").EnumerateArray())
		{
			long id = entry.GetProperty("id").GetInt64();
			Assert.True(id > previous);
			previous = id;
		}

		// A level floor filters Information out.
		JsonElement errorsOnly = await _client.GetFromJsonAsync<JsonElement>(
			"/admin/api/logs?sinceMinutes=60&level=Error");
		foreach (JsonElement entry in errorsOnly.GetProperty("entries").EnumerateArray())
			Assert.Contains(entry.GetProperty("level").GetString(), new[] { "Error", "Fatal" });

		Assert.Equal(HttpStatusCode.BadRequest,
			(await _client.GetAsync("/admin/api/logs?level=Loud")).StatusCode);
	}

	[Fact]
	public async Task StateAndSummary_ReturnLiveShapes()
	{
		JsonElement state = await _client.GetFromJsonAsync<JsonElement>("/admin/api/state");
		Assert.Equal(JsonValueKind.Array, state.GetProperty("sessions").ValueKind);
		Assert.Equal(JsonValueKind.Array, state.GetProperty("watchers").ValueKind);
		Assert.Equal(JsonValueKind.Array, state.GetProperty("longPolls").ValueKind);

		JsonElement summary = await _client.GetFromJsonAsync<JsonElement>("/admin/api/summary");
		Assert.True(summary.GetProperty("declaredUsers").GetInt32() >= 1); // the admin itself
		Assert.True(summary.GetProperty("devices").GetInt32() >= 0);
	}
}
