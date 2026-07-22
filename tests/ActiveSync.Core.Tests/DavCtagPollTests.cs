using ActiveSync.Backends.Dav;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H12: a transient ctag/sync-token read failure must be treated as "unknown", never "changed".
///   The old poll stuffed a sentinel into the map, so a failed read compared unequal to the real
///   baseline and forced a full re-sync on every DAV hiccup. The Ping entry check and watchdog are
///   the correctness guarantee, so a key we could not read stays silent.
/// </summary>
public sealed class DavCtagPollTests
{
	[Fact]
	public void DetectChanges_TransientReadFailure_IsNotReportedAsChanged()
	{
		string[] keys = ["caldav:a", "caldav:b"];
		Dictionary<string, string?> baseline = new() { ["caldav:a"] = "ctag-1", ["caldav:b"] = "ctag-9" };
		// This cycle, folder a failed to read; b is unchanged.
		Dictionary<string, string?> current = new() { ["caldav:b"] = "ctag-9" };
		HashSet<string> baselineFailed = new();
		HashSet<string> currentFailed = new() { "caldav:a" };

		List<string> changed = DavDiscovery.DetectChanges(keys, baseline, current, baselineFailed, currentFailed);

		Assert.Empty(changed);
	}

	[Fact]
	public void DetectChanges_BaselineReadFailure_IsNotReportedAsChanged()
	{
		string[] keys = ["caldav:a"];
		// Baseline read failed (a not in map); current read a real ctag — still unknown, not changed.
		Dictionary<string, string?> baseline = new();
		Dictionary<string, string?> current = new() { ["caldav:a"] = "ctag-1" };

		List<string> changed = DavDiscovery.DetectChanges(
			keys, baseline, current, baselineFailed: new HashSet<string> { "caldav:a" },
			currentFailed: new HashSet<string>());

		Assert.Empty(changed);
	}

	[Fact]
	public void DetectChanges_RealCtagChange_IsReported()
	{
		string[] keys = ["caldav:a", "caldav:b"];
		Dictionary<string, string?> baseline = new() { ["caldav:a"] = "ctag-1", ["caldav:b"] = "ctag-9" };
		Dictionary<string, string?> current = new() { ["caldav:a"] = "ctag-2", ["caldav:b"] = "ctag-9" };

		List<string> changed = DavDiscovery.DetectChanges(
			keys, baseline, current, baselineFailed: new HashSet<string>(), currentFailed: new HashSet<string>());

		Assert.Equal(["caldav:a"], changed);
	}
}
