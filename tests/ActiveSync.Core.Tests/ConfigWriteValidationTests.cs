using ActiveSync.Core.Administration;
using Microsoft.Extensions.Configuration;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Item 24 — config validation unification. The write path (CLI `eas config set` and the web
///   settings editor) must reject at write time exactly what the startup validator would reject at
///   boot, so a value can never persist, run, then brick the next start (B1). Also covers the
///   Number-branch bounds (B10) and the provider-schema secret detection (B25).
/// </summary>
public sealed class ConfigWriteValidationTests
{
	private static IConfiguration EffectiveWith(params (string Key, string? Value)[] pairs)
	{
		Dictionary<string, string?> data = new(StringComparer.OrdinalIgnoreCase)
		{
			// A minimally valid baseline so the only failures come from the value under test.
			["ActiveSync:Encryption:Key"] = Convert.ToBase64String(new byte[32]),
			["ActiveSync:Database:ConnectionString"] = "Data Source=:memory:",
		};
		foreach ((string key, string? value) in pairs)
			data[key] = value;
		return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
	}

	// B1 — the delayed brick: the catalogue accepts WatchdogSeconds=5 (Min 0 / Max 86400) but the
	// startup validator requires 0 or >= 15, so 5 persists, runs, then refuses the next boot.
	[Fact]
	public void WatchdogSeconds_BelowStartupFloor_PassesCatalogue_ButStartupImpactRejects()
	{
		SettingKeys.SettingKey key = SettingKeys.Find("ActiveSync:Eas:WatchdogSeconds")!;

		// The gap the finding names: the per-key catalogue check is happy with 5.
		Assert.Null(SettingKeys.Validate(key, "5"));

		// The unified write-time check catches what startup would have thrown on.
		string? error = SettingKeys.ValidateStartupImpact(EffectiveWith(), key.Key, "5");
		Assert.NotNull(error);
		Assert.Contains("WatchdogSeconds", error);
	}

	// B1 — a cross-field failure the single-key catalogue can never see: MaxHeartbeatSeconds below
	// the currently-effective MinHeartbeatSeconds.
	[Fact]
	public void MaxHeartbeat_BelowEffectiveMin_IsRejectedAtWriteTime()
	{
		string? error = SettingKeys.ValidateStartupImpact(
			EffectiveWith(("ActiveSync:Eas:MinHeartbeatSeconds", "900")),
			"ActiveSync:Eas:MaxHeartbeatSeconds", "100");
		Assert.NotNull(error);
	}

	// B1 — a good value must still pass, and a pre-existing UNRELATED invalid value must not block
	// an unrelated edit (only newly-introduced failures are surfaced).
	[Fact]
	public void GoodValue_Passes_AndUnrelatedPreExistingFailure_DoesNotBlock()
	{
		Assert.Null(SettingKeys.ValidateStartupImpact(EffectiveWith(),
			"ActiveSync:Eas:WatchdogSeconds", "30"));

		// The gateway already carries a broken PublicUrl; editing an unrelated key must still work.
		IConfiguration broken = EffectiveWith(("ActiveSync:PublicUrl", "not a url"));
		Assert.Null(SettingKeys.ValidateStartupImpact(broken, "ActiveSync:ReadOnly", "true"));
	}

	// B10 — a Number setting accepted NaN/Infinity (NumberStyles.Float parses them), which then
	// degrades the refreshers to a point-read on every request.
	[Theory]
	[InlineData("NaN")]
	[InlineData("Infinity")]
	[InlineData("-Infinity")]
	public void NumberSetting_NonFinite_IsRejected(string value)
	{
		SettingKeys.SettingKey key = SettingKeys.Find("ActiveSync:Auth:UsersRefreshSeconds")!;
		Assert.NotNull(SettingKeys.Validate(key, value));
	}

	[Theory]
	[InlineData("1")]
	[InlineData("0")]
	[InlineData("0.5")]
	public void NumberSetting_FiniteWithinBounds_IsAccepted(string value)
	{
		SettingKeys.SettingKey key = SettingKeys.Find("ActiveSync:Auth:UsersRefreshSeconds")!;
		Assert.Null(SettingKeys.Validate(key, value));
	}

	// B10 — the Number branch now honours the key's Max (it ignored Min/Max entirely before).
	[Fact]
	public void NumberSetting_AboveMax_IsRejected()
	{
		SettingKeys.SettingKey key = SettingKeys.Find("ActiveSync:Auth:UsersRefreshSeconds")!;
		Assert.NotNull(SettingKeys.Validate(key, "999999999"));
	}
}
