using AlphaLab.Evaluation.Calibration;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// D103: `noedge_curve_breach_validate` is a RATE PER EVALUATION OPPORTUNITY, not the fraction of plants
/// that ever breach.
///
/// `CONFIG_REFERENCE` declares `NoEdgeCurveBreachMaxFrac` as "the curves' own out-of-sample false-alarm
/// RATE", and `CurveBuilder` builds P_noise AS the `falseAlarmRate` quantile — so the declared quantity is
/// per-point by construction. The shipped implementation instead asked "did this plant EVER sustain a
/// breach anywhere in validate", a LIFETIME probability that can only rise as the validate window grows,
/// compared against a fixed bound. Generation 1 measured 3/50 and passed; generation 2, on a window of the
/// same length but cleaner noise, measured 6/50 and failed — and the well-specified point-level metric
/// (`noedge_pnoise_breach_validate`) passed comfortably at 2.9% against ~5% on the same paths.
///
/// The first test below is the decision made executable: the same underlying behaviour, observed for
/// longer, must not change the answer. The second shows the predicate it replaced failing exactly that.
/// </summary>
public class D103BreachRateTests
{
    private const int K = 3;   // SustainEvals — three consecutive far-side evals is a sustained breach

    /// <summary>One breach every 10 evals, repeated. Lengthening the window must not move the rate.</summary>
    private static List<bool> Periodic(int cycles)
    {
        var flags = new List<bool>();
        for (var c = 0; c < cycles; c++)
        {
            flags.AddRange([true, true, true]);                          // one sustained breach
            flags.AddRange([false, false, false, false, false, false, false]);
        }
        return flags;
    }

    [Fact]
    public void D103_Rate_IsInvariantToWindowLength()
    {
        var shortWindow = ReplayVerification.SustainRate(Periodic(3), K);
        var longWindow = ReplayVerification.SustainRate(Periodic(30), K);

        var shortRate = shortWindow.Breaches / (double)shortWindow.Opportunities;
        var longRate = longWindow.Breaches / (double)longWindow.Opportunities;

        // A 10x longer observation of the SAME behaviour: the rate holds to within the edge effect of the
        // final partial window, which is the only thing that may differ.
        Assert.True(Math.Abs(shortRate - longRate) < 0.02,
            $"rate moved with window length: {shortRate:P2} (3 cycles) vs {longRate:P2} (30 cycles)");
        Assert.True(longWindow.Opportunities > shortWindow.Opportunities * 8,
            "the long window must actually offer proportionally more opportunities");
    }

    /// <summary>The defect, made executable: the predicate D103 replaced answers the same behaviour
    /// differently purely because there was more of it to look at.</summary>
    [Fact]
    public void D103_TheLifetimePredicateItReplaced_IsNotWindowInvariant()
    {
        // A plant that never sustains in a short window, but does once the window is long enough to contain
        // one streak. Same process, two answers — against a bound that did not move.
        var shortWindow = new List<bool> { true, true, false, true, true, false };
        var longWindow = new List<bool>(shortWindow) { true, true, true };

        Assert.False(ReplayVerification.SustainsEver(shortWindow, K));
        Assert.True(ReplayVerification.SustainsEver(longWindow, K));

        // The rate form reports the same event but as a proportion of the chances it had, so the two
        // windows are comparable numbers rather than a flipped boolean.
        Assert.Equal(0, ReplayVerification.SustainRate(shortWindow, K).Breaches);
        Assert.Equal(1, ReplayVerification.SustainRate(longWindow, K).Breaches);
    }

    [Fact]
    public void D103_Opportunities_AreStartPositions_AndOverlapsCount()
    {
        // 5 points, K=3 -> start positions 0,1,2 = 3 opportunities.
        var (breaches, opportunities) = ReplayVerification.SustainRate([true, true, true, true, true], K);
        Assert.Equal(3, opportunities);
        // All five far-side: every start position is a breach. Overlapping runs are counted deliberately —
        // they are the chances the SAME rule had to fire, and de-duplicating them would re-introduce a
        // dependence on where a streak happened to begin.
        Assert.Equal(3, breaches);
    }

    [Fact]
    public void D103_APathShorterThanTheSustainLength_OffersNoOpportunity()
    {
        // Two points cannot contain a three-eval streak: it contributes NOTHING to either side of the
        // ratio, rather than counting as a clean observation and diluting the rate.
        var (breaches, opportunities) = ReplayVerification.SustainRate([true, true], K);
        Assert.Equal(0, opportunities);
        Assert.Equal(0, breaches);
    }

    [Fact]
    public void D103_NoFarSidePoints_IsAZeroRate_NotAnEmptyOne()
    {
        var (breaches, opportunities) = ReplayVerification.SustainRate([false, false, false, false], K);
        Assert.Equal(2, opportunities);
        Assert.Equal(0, breaches);
    }
}
