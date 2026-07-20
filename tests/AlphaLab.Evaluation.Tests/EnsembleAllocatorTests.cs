using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Allocator;

namespace AlphaLab.Evaluation.Tests;

public class EnsembleAllocatorTests
{
    private static readonly AllocatorOptions Opts = new();   // CONFIG defaults

    private static AllocationInput In(string id, double alpha, double se, bool tooEarly = false, bool suspect = false, double? prior = null) =>
        new(id, alpha, se, tooEarly, suspect, prior);

    [Fact]
    public void FR27_ShortTrack_LooseEstimates_ShrinkToEqualWeight()
    {
        // Big standard errors ⇒ heavy shrinkage ⇒ α̃ ≈ ᾱ ⇒ softmax ≈ equal weight.
        var rows = EnsembleAllocator.Allocate(
        [
            In("a", 1.0, 10.0), In("b", 2.0, 10.0), In("c", 3.0, 10.0), In("d", 4.0, 10.0), In("e", 5.0, 10.0),
        ], Opts).Rows;

        Assert.All(rows, r => Assert.True(Math.Abs(r.Weight - 0.2) < 0.02, $"{r.StrategyId} weight {r.Weight:F3} ~ 0.2"));
    }

    [Fact]
    public void FR27_TightEstimates_TiltTowardHigherAlpha()
    {
        var rows = EnsembleAllocator.Allocate(
        [
            In("low", 1.0, 0.5), In("high", 6.0, 0.5),
        ], Opts).Rows.ToDictionary(r => r.StrategyId);

        Assert.True(rows["high"].Weight > rows["low"].Weight);
        Assert.True(rows["high"].ShrinkWeight > 0.5);   // tight SE ⇒ little shrinkage
    }

    [Fact]
    public void FR27_SuspectDecays_LosesWeightVsAnIdenticalTwin()
    {
        var clean = EnsembleAllocator.Allocate([In("x", 5.0, 0.5), In("y", 5.0, 0.5)], Opts)
            .Rows.Single(r => r.StrategyId == "x").Applied;
        var suspect = EnsembleAllocator.Allocate([In("x", 5.0, 0.5, suspect: true), In("y", 5.0, 0.5)], Opts)
            .Rows.Single(r => r.StrategyId == "x");

        Assert.Contains("suspect_decay", suspect.ClampsBound);
        Assert.True(suspect.Applied < clean, $"suspect applied {suspect.Applied:F3} < clean {clean:F3}");
    }

    [Fact]
    public void FR27_TooEarlyCap_Binds_OnAHighAlphaUnprovenStrategy()
    {
        // A dominant α̂ would grab most of the softmax weight, but TooEarly caps it at floor + tilt cap.
        var rows = EnsembleAllocator.Allocate(
        [
            In("early", 20.0, 0.5, tooEarly: true), In("b", 1.0, 0.5), In("c", 1.0, 0.5),
        ], Opts).Rows.ToDictionary(r => r.StrategyId);

        var cap = (Opts.WeightFloorPct + Opts.TooEarlyTiltCapPts) / 100.0;   // 0.15
        Assert.Contains("too_early_cap", rows["early"].ClampsBound);
        Assert.True(rows["early"].Applied <= cap + 1e-9, $"applied {rows["early"].Applied:F3} ≤ cap {cap}");
    }

    [Fact]
    public void FR27_Band_BlocksASubThresholdMoveFromThePriorWeight()
    {
        // The softmax target is near the prior weight (within BandPts) ⇒ keep the prior, log "band".
        var rows = EnsembleAllocator.Allocate(
        [
            In("a", 3.0, 2.0, prior: 0.50), In("b", 3.0, 2.0, prior: 0.50),
        ], Opts).Rows;

        Assert.All(rows, r => Assert.Contains("band", r.ClampsBound));
        Assert.All(rows, r => Assert.Equal(0.50, r.Applied, 10));
    }

    [Fact]
    public void FR27_Ceiling_CapsADominantStrategy()
    {
        var rows = EnsembleAllocator.Allocate(
        [
            In("dom", 50.0, 0.5), In("b", 1.0, 0.5),
        ], Opts).Rows.ToDictionary(r => r.StrategyId);

        Assert.Contains("ceiling", rows["dom"].ClampsBound);
        Assert.True(rows["dom"].Applied <= Opts.WeightCeilingPct / 100.0 + 1e-9);
    }

    [Fact]
    public void FR27_WeightsAreRenormalizedToOne_AndTheFullVectorIsReconstructible()
    {
        var outcome = EnsembleAllocator.Allocate(
        [
            In("a", 1.0, 1.0), In("b", 3.0, 1.0), In("c", 5.0, 1.0),
        ], Opts);

        Assert.Equal(1.0, outcome.Rows.Sum(r => r.Weight), 9);
        Assert.All(outcome.Rows, r =>
        {
            Assert.True(r.ShrinkWeight is > 0 and <= 1);
            Assert.NotEqual(0.0, r.Target);        // the softmax target is recorded
            Assert.True(r.Applied >= 0);
            Assert.NotNull(r.ClampsBound);
        });
    }

    [Fact]
    public void FR27_RosterAboveTheFloorCap_DegradesGracefully_NoOverAllocation()
    {
        // finding 116: > ⌊100/5⌋ = 20 strategies would over-allocate at a flat 5% floor; the floor scales
        // below 1/N instead, so weights still sum to 1.
        var inputs = Enumerable.Range(0, 30).Select(i => In($"s{i}", i % 5, 3.0)).ToList();
        var outcome = EnsembleAllocator.Allocate(inputs, Opts);
        Assert.Equal(1.0, outcome.Rows.Sum(r => r.Weight), 9);
    }

    [Fact]
    public void FR27_EmptyRoster_IsEmpty() =>
        Assert.Empty(EnsembleAllocator.Allocate([], Opts).Rows);
}
