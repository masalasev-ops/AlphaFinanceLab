using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Numerics;
using AlphaLab.Evaluation.Power;

namespace AlphaLab.Evaluation.Tests;

public class MdeCalculatorTests
{
    private static readonly GateOptions Gate = new();   // CONFIG defaults: 0.95 / 0.80 / NwLagCap 21

    [Fact]
    public void ZSum_At95And80_IsApproximately2Point8()
    {
        Assert.Equal(2.8015852, MdeCalculator.ZSum(0.95, 0.80), 6);   // 1.9599640 + 0.8416212
    }

    [Fact]
    public void Compute_LagSelection_IsMinOfTwiceHorizonAndCap()
    {
        var series = NeweyWestTests.Ar1(300, 0.3, 1);
        Assert.Equal(10, MdeCalculator.Compute(series, maxHorizonDays: 5, Gate).NwLag);   // min(10, 21)
        Assert.Equal(21, MdeCalculator.Compute(series, maxHorizonDays: 15, Gate).NwLag);  // min(30, 21)
        Assert.Equal(2, MdeCalculator.Compute(series, maxHorizonDays: 1, Gate).NwLag);    // min(2, 21)
    }

    [Fact]
    public void Compute_EmptySeries_IsInfinite_SoTheGateReadsTooEarly()
    {
        var mde = MdeCalculator.Compute([], 5, Gate);
        Assert.Equal(double.PositiveInfinity, mde.MdeAnn);
    }

    [Fact]
    public void Compute_SingleObservation_IsInfinite_NotZero()
    {
        // σ_LR is unestimable from one difference — the result must be +∞ (nothing detectable yet),
        // never 0 (which IsInside would read as a decisive verdict for a 1-sample track).
        var mde = MdeCalculator.Compute([0.01], 5, Gate);
        Assert.Equal(double.PositiveInfinity, mde.MdeAnn);
        Assert.True(mde.IsInside(0.5));   // any gap is inside an infinite MDE ⇒ TooEarly
    }

    [Fact]
    public void Compute_ConstantMultiPointSeries_IsZero_ADecisiveDifference()
    {
        // A perfectly constant nonzero daily difference has zero variance ⇒ MDE 0 ⇒ decisively
        // distinguishable. That is correct (unlike the single-observation case above).
        var mde = MdeCalculator.Compute([0.01, 0.01, 0.01, 0.01], 5, Gate);
        Assert.Equal(0.0, mde.MdeAnn, 12);
    }

    [Fact]
    public void Compute_MatchesTheClosedFormAssembly()
    {
        var d = NeweyWestTests.Ar1(200, 0.4, 7);
        var res = MdeCalculator.Compute(d, maxHorizonDays: 5, Gate);
        var lrv = NeweyWest.LongRunVariance(d, 10);
        var expected = MdeCalculator.ZSum(0.95, 0.80) * Math.Sqrt(lrv) * 252.0 / Math.Sqrt(d.Length);
        Assert.Equal(expected, res.MdeAnn, 10);
        Assert.Equal(Math.Sqrt(lrv), res.SigmaLr, 12);
    }

    [Fact]
    public void IsInside_IsTrueForGapsSmallerThanTheMde()
    {
        var d = NeweyWestTests.Ar1(120, 0.2, 3);
        var res = MdeCalculator.Compute(d, 5, Gate);
        Assert.True(res.IsInside(res.MdeAnn * 0.5));
        Assert.False(res.IsInside(res.MdeAnn * 1.5));
    }

    [Fact]
    public void FX_MDE_AR1_YieldsLargerMdeThanItsIidSigmaImplies()
    {
        // D48: a positively-autocorrelated difference series must produce a LARGER MDE than if its daily
        // differences were treated as i.i.d. (lag 0). We hold the SAME series and compare the NW MDE
        // (full Bartlett lag) against the i.i.d. MDE (γ0 only). The NW one must be strictly larger.
        var d = NeweyWestTests.Ar1(500, 0.6, 424242);

        var nw = MdeCalculator.Compute(d, maxHorizonDays: 10, Gate);     // lag = min(20, 21) = 20
        Assert.True(nw.NwLag > 0);

        var iidSigma = Math.Sqrt(NeweyWest.LongRunVariance(d, 0));        // "its i.i.d. σ"
        var iidMde = MdeCalculator.ZSum(0.95, 0.80) * iidSigma * 252.0 / Math.Sqrt(d.Length);

        Assert.True(nw.MdeAnn > iidMde,
            $"NW MDE {nw.MdeAnn:F4} should exceed the i.i.d. MDE {iidMde:F4} for an AR(1) series.");
        Assert.True(nw.SigmaLr > iidSigma);   // the long-run σ picks up the autocorrelation
    }
}
