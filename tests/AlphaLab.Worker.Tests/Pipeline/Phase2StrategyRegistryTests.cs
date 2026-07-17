using AlphaLab.Strategies;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>The two per-account run-settings seams stated in checkpoint 2.9 and supplied by the registry
/// (2.10): cap-weight needs a 100% position cap; equal-weight needs a breadth ceiling above the index.</summary>
public class Phase2StrategyRegistryTests
{
    [Fact]
    public void CapWeight_HoldsTheProxyAtA100PercentCap()
    {
        var cw = Phase2StrategyRegistry.For("buyhold:cw");
        Assert.NotNull(cw);
        Assert.Equal(UniverseScope.CapWeightProxy, cw!.Universe);
        Assert.Equal(1.0, cw.Sizing.PositionCapPct); // a single name can be a full position
    }

    [Fact]
    public void EqualWeight_TradesTheWholeIndexWithABreadthCeilingAboveIt()
    {
        var ew = Phase2StrategyRegistry.For("buyhold:ew");
        Assert.NotNull(ew);
        Assert.Equal(UniverseScope.FullIndex, ew!.Universe);
        Assert.True(ew.Guardrails.MaxConcurrentPositions >= 500); // ≥ any single-index size (holds the whole roster)
    }

    [Fact]
    public void Threshold_TradesTheFullIndexAtDefaults()
    {
        var th = Phase2StrategyRegistry.For("threshold:sma50");
        Assert.NotNull(th);
        Assert.Equal(UniverseScope.FullIndex, th!.Universe);
    }

    [Fact]
    public void UnknownStrategy_ResolvesToNull_NotAGuess()
    {
        Assert.Null(Phase2StrategyRegistry.For("momentum:L126:K21:N40"));
    }
}
