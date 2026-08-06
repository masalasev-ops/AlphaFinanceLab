using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Data.Entities;
using AlphaLab.Strategies;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// The registry that turns a PERSISTED row into a runnable plan (6.2), replacing Phase 2's id switch.
///
/// The two per-account run-settings seams stated at checkpoint 2.9 still hold for the three legacy
/// dummies — cap-weight needs a 100% position cap; equal-weight needs a breadth ceiling above the index
/// — and the row-driven path is exercised beside them, because the whole point of 6.2 is that what runs
/// is derived from what was FROZEN rather than from a hardcoded id.
/// </summary>
public class StrategyRegistryTests
{
    private static StrategyRow Row(string id, string family, string configJson) => new()
    {
        StrategyId = id, Family = family, ConfigJson = configJson,
        ExitPolicyJson = "{}", CreatedOn = "2026-08-06", Status = "candidate",
    };

    [Fact]
    public void CapWeight_HoldsTheProxyAtA100PercentCap()
    {
        var cw = StrategyRegistry.ForRow(Row("buyhold:cw", "passive", "{}"));
        Assert.NotNull(cw);
        Assert.Equal(UniverseScope.CapWeightProxy, cw!.Universe);
        Assert.Equal(1.0, cw.Sizing.PositionCapPct); // a single name can be a full position
    }

    [Fact]
    public void EqualWeight_TradesTheWholeIndexWithABreadthCeilingAboveIt()
    {
        var ew = StrategyRegistry.ForRow(Row("buyhold:ew", "passive", "{}"));
        Assert.NotNull(ew);
        Assert.Equal(UniverseScope.FullIndex, ew!.Universe);
        Assert.True(ew.Guardrails.MaxConcurrentPositions >= 500); // holds the whole roster
    }

    [Fact]
    public void Threshold_TradesTheFullIndexAtDefaults()
    {
        var th = StrategyRegistry.ForRow(Row("threshold:sma50", "passive", "{}"));
        Assert.NotNull(th);
        Assert.Equal(UniverseScope.FullIndex, th!.Universe);
    }

    /// <summary>
    /// Rule 10, and the behaviour `RunDay_UnknownStrategyAccount_Skipped_RunStillCommits` pins: a family
    /// this build cannot construct resolves to null so the caller skips it with a reason. It never
    /// guesses a model, and it never falls back to some other family's.
    /// </summary>
    [Fact]
    public void UnknownFamily_ResolvesToNull_NotAGuess()
    {
        Assert.Null(StrategyRegistry.ForRow(Row("momentum:L126:K21:N40", "momentum", "{}")));
    }

    /// <summary>
    /// The legacy dummies are resolved by id BECAUSE their rows were frozen before D133 made config_json
    /// readable — they carry none of the run settings a row-driven family reads, and D17 forbids
    /// re-serializing over a frozen row to add them. This pins that the fallback is a CLOSED set: an
    /// unknown id with an unknown family gets nothing, even with a perfectly readable config.
    /// </summary>
    [Fact]
    public void TheLegacyFallbackIsAClosedSet_AReadableConfigDoesNotOpenIt()
    {
        var readable = StrategyConfigJson.Write(new StrategyConfig
        {
            Seed = 1, Selection = SelectionRule.TopN(40), Sizing = SizingMode.Equal,
        });

        Assert.Null(StrategyRegistry.ForRow(Row("buyhold:something_else", "passive", readable)));
    }
}
