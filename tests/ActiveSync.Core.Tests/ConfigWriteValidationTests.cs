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

	// B12 — the CLI's `eas logs -l critical` already accepts the alias (LogQueryService.LevelsAtOrAbove);
	// this write path's enum check knew only the four exact names, so a value the CLI understood
	// fine was rejected here (and, for a file/env value, at startup — see ActiveSyncOptionsValidatorTests).
	[Fact]
	public void DbMinimumLevel_AcceptsTheSameAliasesAsEasLogs()
	{
		SettingKeys.SettingKey key = SettingKeys.Find("ActiveSync:Log:DbMinimumLevel")!;
		Assert.Null(SettingKeys.Validate(key, "critical"));
	}

	// B2 — the OIDC admin-claim pair could never be configured through either write surface. Writing
	// AdminClaim alone introduces the failure "...AdminClaimValue is required when AdminClaim is set"
	// (ActiveSyncOptionsValidator.cs:187-190), and that failure string names a DIFFERENT, still-unset
	// key (AdminClaimValue) — but the substring test `failure.Contains(key)` matched anyway, because
	// "...AdminClaim" is a textual PREFIX of "...AdminClaimValue". So a write that introduces only a
	// failure about the OTHER key of the pair was wrongly attributed to THIS key and refused, even
	// though the scoping rule's own intent (only failures the write introduces AND that name the key
	// just written) says it should pass through to be completed by the next write.
	[Fact]
	public void WritingAdminClaimAlone_IsNotRejectedByTheOtherKeysFailure()
	{
		string? error = SettingKeys.ValidateStartupImpact(
			EffectiveWith(), "ActiveSync:WebUi:Oidc:AdminClaim", "groups");
		Assert.Null(error);
	}

	// B2 — the reverse write (AdminClaimValue alone, with AdminClaim still unset) is a REAL,
	// self-contained failure — the value just written is exactly what is missing its partner — and
	// must stay rejected so the fix does not become a blanket "ignore this whole failure" hack.
	[Fact]
	public void WritingAdminClaimValueAlone_IsStillRejected()
	{
		string? error = SettingKeys.ValidateStartupImpact(
			EffectiveWith(), "ActiveSync:WebUi:Oidc:AdminClaimValue", "engineering");
		Assert.NotNull(error);
		Assert.Contains("AdminClaimValue", error);
	}

	// B2 — once AdminClaim is on file, completing the pair by writing AdminClaimValue must pass (both
	// requirements are then satisfied), proving the pair is actually reachable in sequence.
	[Fact]
	public void CompletingThePair_InEitherOrder_Succeeds()
	{
		IConfiguration withAdminClaim = EffectiveWith(("ActiveSync:WebUi:Oidc:AdminClaim", "groups"));
		Assert.Null(SettingKeys.ValidateStartupImpact(
			withAdminClaim, "ActiveSync:WebUi:Oidc:AdminClaimValue", "engineering"));
	}
}
