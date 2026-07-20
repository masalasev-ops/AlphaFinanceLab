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
    public void FR27_TooEarlyCap_BoundsTheMoveFromThePrior_InBothDirections()
    {
        var cap = Opts.TooEarlyTiltCapPts / 100.0;   // 0.10

        // A huge alpha with a small prior tilts up only to prior + cap (NOT an absolute 0.15).
        var up = EnsembleAllocator.Allocate(
            [In("early", 30.0, 0.5, tooEarly: true, prior: 0.02), In("b", 1.0, 0.5, prior: 0.49)], Opts)
            .Rows.Single(r => r.StrategyId == "early");
        Assert.Contains("too_early_cap", up.ClampsBound);
        Assert.True(up.Applied <= 0.02 + cap + 1e-9, $"applied {up.Applied:F3} ≤ prior+cap {0.02 + cap}");

        // A large negative alpha is bounded on the DOWNSIDE too — to prior − cap (the old code never did this).
        var down = EnsembleAllocator.Allocate(
            [In("early", -30.0, 0.5, tooEarly: true, prior: 0.50), In("b", 1.0, 0.5, prior: 0.50)], Opts)
            .Rows.Single(r => r.StrategyId == "early");
        Assert.Contains("too_early_cap", down.ClampsBound);
        Assert.True(down.Applied >= 0.50 - cap - 1e-9, $"applied {down.Applied:F3} ≥ prior−cap {0.50 - cap}");
    }

    [Fact]
    public void FR27_Suspect_DecaysFromThePrior_AndNeverGains()
    {
        // A dominant alpha (would grab the ceiling) with a Suspect flag must DECAY from its prior weight,
        // never rise toward the target — the old code let a Suspect strategy gain to a dominant weight.
        var s = EnsembleAllocator.Allocate(
            [In("s", 40.0, 0.5, suspect: true, prior: 0.40), In("b", 1.0, 0.5, prior: 0.30)], Opts)
            .Rows.Single(r => r.StrategyId == "s");

        Assert.Contains("suspect_decay", s.ClampsBound);
        Assert.True(s.Applied <= 0.40 + 1e-9, $"suspect applied {s.Applied:F3} must never exceed its prior 0.40");
        Assert.True(s.Applied < 0.40, "a Suspect strategy should have decayed below its prior");
    }

    [Fact]
    public void FR27_Suspect_SubBandDecay_StillApplies_NotRevertedByTheBand()
    {
        // A Suspect strategy near the floor (prior 0.10) decays to 0.075 — a sub-band move. The band must
        // NOT revert this deliberate de-risk to the prior (the bug the review caught).
        var s = EnsembleAllocator.Allocate(
            [In("s", 5.0, 0.5, suspect: true, prior: 0.10), In("b", 5.0, 0.5, prior: 0.90)], Opts)
            .Rows.Single(r => r.StrategyId == "s");

        Assert.Contains("suspect_decay", s.ClampsBound);
        Assert.Equal(0.10 * (1 - Opts.SuspectDecayPctPerEval / 100.0), s.Applied, 9);   // exactly prior × 0.75 = 0.075
        Assert.True(s.Applied < 0.10, "the sub-band Suspect decay must apply, not be reverted to the prior");
    }

    [Fact]
    public void FR27_Band_SupraBandMove_StepsOnlyToTheBandEdge()
    {
        // prior 0.10, a far-higher target ⇒ move only to the band edge prior + BandPts (0.15), not the target.
        var a = EnsembleAllocator.Allocate(
            [In("a", 20.0, 0.5, prior: 0.10), In("b", 1.0, 0.5, prior: 0.90)], Opts)
            .Rows.Single(r => r.StrategyId == "a");

        Assert.Contains("band", a.ClampsBound);
        Assert.Equal(0.10 + Opts.BandPts / 100.0, a.Applied, 9);   // 0.15 — the band edge, not the softmax target
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
