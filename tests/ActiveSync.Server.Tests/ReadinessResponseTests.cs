using System.Text.Json;
using ActiveSync.Server.Setup;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E16: /readyz must not disclose the configured backend topology (the component role names) to
///   anonymous, non-local callers on the phone-facing listener. The verdict travels in the HTTP
///   status; only a local caller gets the per-component detail.
/// </summary>
public sealed class ReadinessResponseTests
{
	private static readonly Dictionary<string, bool> Components = new()
	{
		["database"] = true,
		["mailstore"] = true,
		["calendar"] = false
	};

	[Fact]
	public void Body_WithoutDetail_OmitsTheComponentTopology()
	{
		string json = JsonSerializer.Serialize(ReadinessResponse.Body(true, Components, includeDetail: false));

		Assert.Contains("\"status\":\"ready\"", json);
		Assert.DoesNotContain("components", json);
		Assert.DoesNotContain("mailstore", json);
		Assert.DoesNotContain("calendar", json);
	}

	[Fact]
	public void Body_WithDetail_KeepsTheComponentMap()
	{
		string json = JsonSerializer.Serialize(ReadinessResponse.Body(false, Components, includeDetail: true));

		Assert.Contains("\"status\":\"not ready\"", json);
		Assert.Contains("mailstore", json);
		Assert.Contains("calendar", json);
	}
}
