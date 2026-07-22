using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H21: the shared WebDAV client took its settings (BaseUrl/TLS) from the Calendar role but its
///   credentials from <c>Roles[0]</c>. When the Tasks role was assigned first with its own per-user
///   credentials, the client hit the calendar endpoint authenticating as the tasks role. The client
///   role must supply both, so credentials always match the endpoint being talked to.
/// </summary>
public sealed class CalDavBackendProviderTests
{
	[Fact]
	public void SelectClientRole_UsesCalendarCredentials_EvenWhenTasksIsListedFirst()
	{
		ResolvedRole tasks = Role(BackendRole.Tasks, "tasks-user");
		ResolvedRole calendar = Role(BackendRole.Calendar, "cal-user");

		ResolvedRole picked = CalDavBackendProvider.SelectClientRole([tasks, calendar]);

		Assert.Equal(BackendRole.Calendar, picked.Role);
		Assert.Equal("cal-user", picked.Credentials.UserName);
	}

	[Fact]
	public void SelectClientRole_FallsBackToTheFirstRole_WhenNoCalendar()
	{
		ResolvedRole tasks = Role(BackendRole.Tasks, "tasks-user");

		ResolvedRole picked = CalDavBackendProvider.SelectClientRole([tasks]);

		Assert.Equal(BackendRole.Tasks, picked.Role);
	}

	private static ResolvedRole Role(BackendRole role, string user)
	{
		ProviderSettings settings = ProviderSettings.FromFlat(
			new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com/" });
		return new ResolvedRole(role, "caldav", settings, new BackendCredentials(user, "pw"));
	}
}
