using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Json;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The Phase-2 domain contracts: identity (D39/rule 2), horizon, frozen config (D17), and the
/// FR-11-partial sizing surface. These types carry rules the rest of the phase relies on.
/// </summary>
public class DomainContractTests
{
    [Fact]
    public void FR8_SecurityId_IsDistinctFromABareLong()
    {
        // Rule 2: all identity is security_id. account_id, run_id, and a ticker-derived int are
        // all longs too — wrapping is what stops them binding to a security_id parameter.
        Assert.Equal(42L, (long)new SecurityId(42));
        Assert.Equal(new SecurityId(42), new SecurityId(42));
        Assert.NotEqual(new SecurityId(42), new SecurityId(43));
        Assert.Equal("42", new SecurityId(42).ToString());
    }

    [Fact]
    public void FR8_HoldingHorizon_MapsOnlyFixedHoldsToADayCount()
    {
        // strategies.holding_horizon_days is nullable precisely because two shapes have no day
        // count. Coercing them to 0 would make "hold to next rebalance" read as "hold zero days".
        Assert.Equal(10, new HoldingHorizon.Days(10).Days_);
        Assert.Null(new HoldingHorizon.ToRankExit().Days_);
        Assert.Null(new HoldingHorizon.ToNextRebalance().Days_);
    }

    [Fact]
    public void FR8_StrategyConfig_MissingParam_Throws_NeverDefaults()
    {
        // D17: params are frozen. A missing lookback is a config error; defaulting it to 0 would
        // silently run a different strategy than the one registered.
        var config = new StrategyConfig
        {
            Seed = 1,
            Selection = SelectionRule.TopN(40),
            Sizing = SizingMode.Equal,
            Params = new Dictionary<string, double> { ["lookback"] = 126 },
        };

        Assert.Equal(126, config.Param("lookback"));
        var ex = Assert.Throws<KeyNotFoundException>(() => config.Param("skip"));
        Assert.Contains("skip", ex.Message);
    }

    [Fact]
    public void FR28_StrategyConfig_DefaultsToRegistered_SoUnregisteredIsAlwaysDeliberate()
    {
        // Hard rule 16 / D52: the 'unregistered' marker is rendered permanently on the strategy
        // card. Defaulting it to true would silently brand every strategy; defaulting to false
        // means a candidate is only ever unregistered because a caller said so.
        var config = new StrategyConfig
        {
            Seed = 1,
            Selection = SelectionRule.TopN(40),
            Sizing = SizingMode.Equal,
        };

        Assert.False(config.Unregistered);
    }

    [Theory]
    [InlineData(SizingMode.Equal, "\"equal\"")]
    [InlineData(SizingMode.InverseVol, "\"inverse_vol\"")]
    [InlineData(SizingMode.Kelly, "\"kelly\"")]
    public void FR11_SizingMode_SerializesAsTheConfigToken(SizingMode mode, string expected)
    {
        // The enum must round-trip the exact tokens CONFIG_REFERENCE documents, or a config
        // value would silently fail to bind to the mode it names.
        Assert.Equal(expected, JsonSerializer.Serialize(mode, AlphaLabJson.Options));
    }

    [Fact]
    public void FR8_SelectionRule_DefaultsMatchTheCatalog()
    {
        // Catalog §3: Threshold's minScore default is 0.60; momentum's TopN breadth is 40.
        Assert.Equal(40, SelectionRule.TopN(40).N);
        Assert.Equal(0.60, SelectionRule.TopN(40).MinScore);

        var threshold = SelectionRule.Threshold(0.60, 60);
        Assert.Equal(SelectionMode.Threshold, threshold.Mode);
        Assert.Equal(0.60, threshold.MinScore);
        Assert.Equal(60, threshold.MaxConcurrent);
    }

    [Fact]
    public void FR10_CostsOptions_DefaultsMirrorConfigReference()
    {
        // CONFIG_REFERENCE is the ONLY source of truth for defaults. A drift here would price
        // every fill in the lab differently from what the documented config claims.
        var costs = new CostsOptions();

        Assert.Equal("cm-1.0", costs.ModelVersion);
        Assert.Equal(0.0m, costs.CommissionPerTrade);
        Assert.Equal(1.0, costs.HalfSpreadBpByBucket.Mega);
        Assert.Equal(2.5, costs.HalfSpreadBpByBucket.Large);
        Assert.Equal(5.0, costs.HalfSpreadBpByBucket.Other);
        Assert.Equal(4.0e8, costs.BucketAdvUsdThresholds.Mega);
        Assert.Equal(1.0e8, costs.BucketAdvUsdThresholds.Large);
        Assert.Equal(0.1, costs.ImpactK);
        Assert.Equal(21, costs.AdvWindowDays);
        Assert.Equal(2.0, costs.ParticipationCapPctAdv);
    }

    [Fact]
    public void FR10_CostsOptions_CommissionIsDecimal_NotDouble()
    {
        // D69 / hard rule: ledger money is decimal end-to-end. A double commission would
        // reintroduce binary-float error on the one number the whole falsification rests on.
        Assert.Equal(typeof(decimal), typeof(CostsOptions).GetProperty(nameof(CostsOptions.CommissionPerTrade))!.PropertyType);
    }

    [Fact]
    public void FR11_SizingOptions_DefaultsToTheModeThisBuildCanActuallyRun()
    {
        // Finding 169: CONFIG_REFERENCE documents inverse_vol as the designed end state (Phase 6).
        // The code default is Equal so a config with no Sizing section is honest rather than
        // claiming a mode the sizer would throw on.
        Assert.Equal(SizingMode.Equal, new SizingOptions().Mode);
        Assert.Equal(0.05, new SizingOptions().PositionCapPct);
    }

    [Fact]
    public void FR11_SizingOptions_CarriesThePhase6SurfaceForConfigFidelity()
    {
        // Carried, not read. Present so the Sizing section binds whole rather than silently
        // dropping documented keys.
        var sizing = new SizingOptions();

        Assert.Equal("ledoit_wolf", sizing.Covariance.Estimator);
        Assert.Equal(252, sizing.Covariance.WindowDays);
        Assert.Equal(0.97, sizing.Covariance.EwmaLambda);
        Assert.Equal(0.25, sizing.Kelly.FractionCap);
        Assert.Equal(0.12, sizing.PortfolioVolTargetAnn);
    }

    [Fact]
    public void FR8_GuardrailsOptions_DefaultsMirrorConfigReference()
    {
        var guardrails = new GuardrailsOptions();

        Assert.Equal(0.0, guardrails.MinScore);
        Assert.Equal(60, guardrails.MaxConcurrentPositions);
        Assert.Equal(0.15, guardrails.HeatMaxPredictedVolAnn);
        Assert.Equal(3, guardrails.ReentryCooldownDays);
        Assert.Equal(25.0, guardrails.DrawdownCircuitBreakerPct);
    }
}
