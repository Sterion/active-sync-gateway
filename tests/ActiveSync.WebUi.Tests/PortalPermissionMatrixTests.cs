using System.Net;
using System.Net.Http.Json;
using ActiveSync.Contracts;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   The permission table for per-field user resolution — the security property of the whole
///   design, since there is one stored slot per field and admin/holder write access differs only
///   by WHICH FIELDS each may write, never by storage:
///   <list type="bullet">
///     <item>Enabled / Provider (serving topology) — admin only</item>
///     <item>Backend credentials (UserName / Password) — both</item>
///     <item>Settings keys the provider marks SelfServiceEditable — both; all others admin only</item>
///     <item>User-level fields (Admin, Enabled, OidcSubject, AutoProvisioned) — admin only</item>
///     <item>Gateway Password — both (the holder with a current-password check)</item>
///   </list>
///   Asserted here across every role, so a role added later cannot quietly acquire a
///   self-service surface it was never meant to have.
/// </summary>
public sealed class PortalPermissionMatrixTests
{
	// The web-UI test host registers the DAV + local providers, so the matrix is driven over
	// every DAV-served role. The permission logic is role-agnostic (it reads the provider's
	// schema), so this covers the rule rather than one role's wiring.
	private static readonly Dictionary<string, string?> AllRoles = new()
	{
		["ActiveSync:Backends:Calendar:Provider"] = "caldav",
		["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.example.com",
		["ActiveSync:Backends:Tasks:Provider"] = "caldav",
		["ActiveSync:Backends:Tasks:BaseUrl"] = "https://dav.example.com",
		["ActiveSync:Backends:Contacts:Provider"] = "carddav",
		["ActiveSync:Backends:Contacts:BaseUrl"] = "https://dav.example.com",
	};

	/// <summary>Every role the portal exposes an editor for in this host.</summary>
	public static TheoryData<string> EditableRoles() =>
		[nameof(BackendRole.Calendar), nameof(BackendRole.Tasks), nameof(BackendRole.Contacts)];

	private static Dictionary<string, UserOptions> Bob() =>
		WebUiHost.Users(("bob", new UserOptions { MailAddress = "bob@example.com" }));

	// ---- serving topology: admin only ----

	[Theory]
	[MemberData(nameof(EditableRoles))]
	public async Task Holder_CannotSwitchTheProvider_ForAnyRole(string role)
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Bob(), AllRoles);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync($"/user/api/backends/{role}", new
		{
			provider = "local",
			userName = "bob-backend",
		});

		// The request may succeed (the credential part is legitimate), but the topology field is
		// NOT applied — the portal deliberately never reads it.
		Assert.True(response.IsSuccessStatusCode, $"PUT failed: {response.StatusCode}");
		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		Assert.Null(stored?.Backends?.GetValueOrDefault(role)?.Provider);
	}

	[Theory]
	[MemberData(nameof(EditableRoles))]
	public async Task Holder_CannotTurnARoleOff_ForAnyRole(string role)
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Bob(), AllRoles);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync($"/user/api/backends/{role}", new
		{
			enabled = false,
			userName = "bob-backend",
		});

		Assert.True(response.IsSuccessStatusCode, $"PUT failed: {response.StatusCode}");
		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		Assert.Null(stored?.Backends?.GetValueOrDefault(role)?.Enabled);
	}

	// ---- backend credentials: the holder's own to manage ----

	[Theory]
	[MemberData(nameof(EditableRoles))]
	public async Task Holder_MaySetTheirOwnBackendCredentials_ForAnyRole(string role)
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Bob(), AllRoles);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync($"/user/api/backends/{role}", new
		{
			userName = "bob-backend",
			password = "backend-secret",
		});

		Assert.True(response.IsSuccessStatusCode, $"PUT failed: {response.StatusCode}");
		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		BackendRoleOverride? saved = stored?.Backends?.GetValueOrDefault(role);
		Assert.Equal("bob-backend", saved?.UserName);
		Assert.NotNull(saved?.Password);
		// Stored as a backend secret (sealed or plaintext), never as a pbkdf2$ hash a backend
		// could not present.
		Assert.DoesNotContain("pbkdf2$", saved!.Password!);
	}

	// ---- user-level fields: admin only, and the portal exposes no route at all ----

	[Fact]
	public async Task Holder_HasNoRouteToUserLevelFields()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Bob(), AllRoles);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		// The admin surface is the only place these live, and it is admin-gated.
		foreach (string path in new[] { "/admin/api/users/bob", "/admin/api/users" })
		{
			HttpResponseMessage response = await client.PutAsJsonAsync(path, new
			{
				admin = true, enabled = true, mailAddress = "bob@example.com",
			});
			Assert.True(
				response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
					or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
				$"{path} answered {response.StatusCode} for a non-admin");
		}

		// ...and nothing about the user changed.
		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		Assert.NotEqual(true, stored?.Admin);
	}

	[Fact]
	public async Task Holder_CannotEscalateToAdmin_ThroughTheirOwnBackendEditor()
	{
		// The role editor is the holder's one write surface; posting user-level shapes at it must
		// not reach them.
		await using WebUiHost host = await WebUiHost.StartAsync(Bob(), AllRoles);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			userName = "bob-backend",
			admin = true,          // not part of the role shape
			enabled = true,
			oidcSubject = "attacker-subject",
		});

		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		Assert.NotEqual(true, stored?.Admin);
		Assert.Null(stored?.OidcSubject);
	}

	[Fact]
	public async Task Holder_CannotSetTheUserWideBackendDefaults()
	{
		// DefaultBackendLogin/Password apply to EVERY role, so they are administered: a holder
		// setting them would change the credential the gateway presents everywhere at once (and
		// the default password additionally pins their own authentication).
		await using WebUiHost host = await WebUiHost.StartAsync(Bob(), AllRoles);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			userName = "bob-backend",
			defaultBackendLogin = "someone-else",
			defaultBackendPassword = "someone-elses-secret",
		});

		UserOptions? stored = await new UserStore(host.Factory).GetAsync("bob", CancellationToken.None);
		Assert.Null(stored?.DefaultBackendLogin);
		Assert.Null(stored?.DefaultBackendPassword);
	}

	// ---- one slot, both writers ----

	[Fact]
	public async Task AdminAndHolder_WriteTheSameSlot_LastWriteWins()
	{
		// Decision 9: there is ONE stored value per field. The admin may overwrite the holder's
		// value and the holder may overwrite the admin's — the difference between them is
		// permission, not storage.
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(
				("alice", new UserOptions { Admin = true }),
				("bob", new UserOptions { MailAddress = "bob@example.com" })),
			AllRoles);

		using (HttpClient holder = await host.SignInAsync("bob", admin: false))
		{
			HttpResponseMessage response = await holder.PutAsJsonAsync("/user/api/backends/Calendar", new
			{
				userName = "set-by-holder",
			});
			Assert.True(response.IsSuccessStatusCode);
		}

		UserStore store = new(host.Factory);
		Assert.Equal("set-by-holder",
			(await store.GetAsync("bob", CancellationToken.None))?.Backends?["Calendar"].UserName);

		using (HttpClient admin = await host.SignInAsync("alice", admin: true))
		{
			HttpResponseMessage response = await admin.PutAsJsonAsync("/admin/api/users/bob", new
			{
				enabled = true,
				backends = new Dictionary<string, object>
				{
					["Calendar"] = new { userName = "set-by-admin" },
				},
			});
			Assert.True(response.IsSuccessStatusCode, $"admin PUT failed: {response.StatusCode}");
		}

		Assert.Equal("set-by-admin",
			(await store.GetAsync("bob", CancellationToken.None))?.Backends?["Calendar"].UserName);

		// ...and the holder can take it back — same slot, last write wins in either direction.
		using (HttpClient holder = await host.SignInAsync("bob", admin: false))
			await holder.PutAsJsonAsync("/user/api/backends/Calendar", new { userName = "holder-again" });

		Assert.Equal("holder-again",
			(await store.GetAsync("bob", CancellationToken.None))?.Backends?["Calendar"].UserName);
	}
}
