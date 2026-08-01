using AlphaLab.Evaluation.Numerics;
using AlphaLab.Evaluation.Signals;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// FX-SignalEffectiveN (D108) and the Student-t reference underneath it.
///
/// D108 promoted the effective independent sample from a printed caveat to a load-bearing INPUT: it
/// sets df, df sets the critical value, and the critical value decides the flag. A quantity that
/// determines a verdict earns a fixture asserting its DERIVATION on hand-computed cases — not merely a
/// display test showing that some number renders.
/// </summary>
public class SignalTrendTests
{
    // 5 trading years ~ 1260 sessions; the D108 window for BOTH horizons.
    private const int FiveYears = 1260;

    [Fact]
    public void FX_SignalEffectiveN_DerivesTheSampleAndBothDegreesOfFreedom()
    {
        // n_eff = window / horizon, floored. The two arms differ because they fit different things:
        // the level arm a MEAN (n-1), the trend arm a SLOPE (n-2) — the asymmetry that made the trend
        // arm the binding constraint in D108.
        var at63 = new EffectiveSample(FiveYears, 63);
        Assert.Equal(20, at63.Count);          // 1260 / 63
        Assert.Equal(19, at63.LevelDf);
        Assert.Equal(18, at63.TrendDf);
        Assert.True(at63.CanInfer);

        var at21 = new EffectiveSample(FiveYears, 21);
        Assert.Equal(60, at21.Count);          // 1260 / 21
        Assert.Equal(59, at21.LevelDf);
        Assert.Equal(58, at21.TrendDf);

        // The REJECTED 1-year window, kept as a fixture so D108's arithmetic stays checkable: ~4
        // observations at k=63, which is what made a normal reference indefensible there.
        var oneYearAt63 = new EffectiveSample(252, 63);
        Assert.Equal(4, oneYearAt63.Count);
        Assert.Equal(3, oneYearAt63.LevelDf);
        Assert.Equal(2, oneYearAt63.TrendDf);

        // A horizon that does not fit the window even once cannot be inferred on — reported, not faked.
        Assert.False(new EffectiveSample(100, 63).CanInfer);
    }

    [Fact]
    public void FX_SignalEffectiveN_CriticalValuesMatchPublishedStudentTQuantiles()
    {
        // The exact numbers D108 rests on, against published two-sided t tables.
        Assert.Equal(2.093024, StudentT.TwoSidedCritical(0.05, 19), 5);   // level arm at 5y/k=63
        Assert.Equal(2.100922, StudentT.TwoSidedCritical(0.05, 18), 5);   // trend arm at 5y/k=63
        Assert.Equal(3.182446, StudentT.TwoSidedCritical(0.05, 3), 5);    // the rejected 1y/k=63 level arm
        Assert.Equal(2.000995, StudentT.TwoSidedCritical(0.05, 59), 5);   // level arm at 5y/k=21

        // One-sided, which is what the directional arms actually use.
        Assert.Equal(1.729133, StudentT.OneSidedCritical(0.05, 19), 5);
        Assert.Equal(2.552380, StudentT.OneSidedCritical(0.01, 18), 5);

        // The claim D108 turns on: a NORMAL reference is materially wrong at ~4 observations and
        // nearly right at ~20. Stated as an assertion so it cannot quietly stop being true.
        var z = 1.959964;
        Assert.True(StudentT.TwoSidedCritical(0.05, 3) > 1.6 * z);    // df=3  => 3.182, +62%
        Assert.True(StudentT.TwoSidedCritical(0.05, 19) < 1.08 * z);  // df=19 => 2.093, +7%
    }

    [Fact]
    public void StudentT_CdfAndQuantile_AreMutualInverses_AndSymmetric()
    {
        foreach (var df in new double[] { 2, 3, 8, 18, 19, 59, 200 })
        {
            foreach (var p in new[] { 0.6, 0.75, 0.9, 0.95, 0.975, 0.99 })
            {
                var t = StudentT.InvCdf(p, df);
                Assert.Equal(p, StudentT.Cdf(t, df), 9);
            }
            Assert.Equal(0.5, StudentT.Cdf(0.0, df), 12);                       // symmetric about zero
            Assert.Equal(-StudentT.InvCdf(0.9, df), StudentT.InvCdf(0.1, df), 9);
        }

        // As df grows the t converges on the normal — the sanity check that the whole correction is
        // about SMALL samples and costs nothing at large ones.
        Assert.Equal(1.959964, StudentT.TwoSidedCritical(0.05, 100000), 4);
    }

    [Fact]
    public void Gone_WhenTheMeanIsNotSignificantlyAboveZero()
    {
        // A pure-noise IC series alternating around zero: the mean is indistinguishable from zero, so
        // the signal is GONE whatever its slope is doing.
        var ic = new List<double>();
        for (var i = 0; i < FiveYears; i++) ic.Add(i % 2 == 0 ? 0.01 : -0.01);

        var v = SignalTrendInference.Infer(ic, 63, FiveYears, goneAlpha: 0.05, decayAlpha: 0.05);
        Assert.Equal(TrendFlag.Gone, v.Flag);
        Assert.Equal(20, v.Sample.Count);
    }

    [Fact]
    public void Stable_WhenTheMeanIsClearlyPositiveAndFlat()
    {
        // A strong, steady IC with realistic noise: significantly above zero and not trending down.
        //
        // The noise must be RANDOM rather than a perfect alternation (finding 306). An alternating
        // series' autocovariances flip sign and very nearly cancel inside the Bartlett sum, so its
        // residual long-run variance collapses toward zero and the tiny endpoint-driven slope of an
        // even-length alternation reads as a significant decay. That fixture only looked "flat" while
        // the slope error was inflated by the double-counted overlap correction; once the error was
        // correct it flagged `decaying`. Seeded, so this is still deterministic.
        var rng = new Random(20260731);
        var ic = new List<double>();
        for (var i = 0; i < FiveYears; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            ic.Add(0.20 + 0.02 * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        var v = SignalTrendInference.Infer(ic, 63, FiveYears, goneAlpha: 0.05, decayAlpha: 0.05);
        Assert.Equal(TrendFlag.Stable, v.Flag);
        Assert.True(v.TStat > 0);
    }

    [Fact]
    public void Decaying_WhenAStrongSignalSlopesDownAcrossTheWindow()
    {
        // Starts strong and decays within the window — the shape the flag exists to catch, and the
        // reason a 5-year window costs less responsiveness than it appears: the TREND arm fires inside
        // the window rather than waiting for the mean to fall (D108).
        var ic = new List<double>();
        for (var i = 0; i < FiveYears; i++) ic.Add(0.30 - 0.20 * i / (FiveYears - 1.0));

        var v = SignalTrendInference.Infer(ic, 63, FiveYears, goneAlpha: 0.05, decayAlpha: 0.05);
        Assert.Equal(TrendFlag.Decaying, v.Flag);
        Assert.True(v.MeanIc > 0);   // still positive on average, yet already flagged
    }

    [Fact]
    public void Insufficient_WhenTheWindowCannotSupportTheHorizon()
    {
        // Honest absence rather than a silent "stable": 100 sessions cannot carry a 63-day horizon.
        var v = SignalTrendInference.Infer([0.1, 0.2, 0.15], 63, 100, 0.05, 0.05);
        Assert.Equal(TrendFlag.Insufficient, v.Flag);
        Assert.Null(v.TStat);
    }

    [Fact]
    public void MinimumEffectiveSample_IsDerivedFromTheSameLegThatRejectedTheOneYearWindow()
    {
        // The identity that makes the floor a derivation rather than a preference: the NW lag IS the
        // horizon, so lag/T = horizon/window = 1/n_eff exactly. D108's two recorded endpoints on that
        // scale are lag/T = 0.25 (n_eff 4, rejected) and 0.05 (n_eff 20, sound); the standard "HAC
        // bandwidth at most about a tenth of the sample" sits between them and inverts to n_eff >= 10.
        Assert.Equal(10, EffectiveSample.MinimumCount);
        Assert.Equal(0.10, 1.0 / EffectiveSample.MinimumCount, 12);
        Assert.True(1.0 / 4 > 1.0 / EffectiveSample.MinimumCount);    // the rejected 1y/k=63 point
        Assert.True(1.0 / 20 < 1.0 / EffectiveSample.MinimumCount);   // the accepted 5y/k=63 point
    }

    [Fact]
    public void BelowTheFloor_NoVerdictIsEmitted_AtTheFloor_OneAppears()
    {
        // A signal with an unmistakable, strongly positive IC. Whether it gets a verdict must depend on
        // the effective sample ALONE — the data is identical either side of the boundary.
        static List<double> StrongIc(int n)
        {
            var ic = new List<double>();
            for (var i = 0; i < n; i++) ic.Add(0.20 + (i % 2 == 0 ? 0.001 : -0.001));
            return ic;
        }

        // One short of the floor at k=63: 9 * 63 = 567 sessions => n_eff 9.
        var below = SignalTrendInference.Infer(StrongIc(600), 63, 63 * (EffectiveSample.MinimumCount - 1), 0.05, 0.05);
        Assert.Equal(TrendFlag.Insufficient, below.Flag);
        Assert.Equal(EffectiveSample.MinimumCount - 1, below.Sample.Count);
        Assert.Null(below.TStat);            // no statistic is published
        Assert.Null(below.LevelCritical);    // and no critical value, because no test was run
        Assert.True(below.MeanIc > 0.19);    // the MEAN is still reported — only the claim is withheld

        // Exactly at the floor, on the same data: a verdict appears.
        var at = SignalTrendInference.Infer(StrongIc(600), 63, 63 * EffectiveSample.MinimumCount, 0.05, 0.05);
        Assert.Equal(EffectiveSample.MinimumCount, at.Sample.Count);
        Assert.NotEqual(TrendFlag.Insufficient, at.Flag);
        Assert.NotNull(at.TStat);
        Assert.NotNull(at.LevelCritical);
    }

    [Fact]
    public void TheRampYearsAreTheLowDfCase_WhichIsWhyTheExactTReferenceMatters()
    {
        // A 5-year window is not full during the backfill's first years, so n_eff RAMPS up to it. The
        // floor is therefore reached in the middle of the operating range, not at some extreme.
        Assert.Equal(EffectiveSample.MinimumCount, new EffectiveSample(63 * 10, 63).Count);   // ~2.5y at k=63
        Assert.Equal(EffectiveSample.MinimumCount, new EffectiveSample(21 * 10, 21).Count);   // ~10mo at k=21

        // And at the floor the small-sample correction is still MATERIAL: trend df = 8, where the
        // ONE-SIDED t exceeds the normal critical value by ~13%. It must be the one-sided tail, because
        // that is the tail SignalTrend actually uses (finding 302) - asserting the two-sided one here
        // would let the flag's reference drift without ever reddening this test.
        var atFloor = new EffectiveSample(63 * EffectiveSample.MinimumCount, 63);
        Assert.Equal(8, atFloor.TrendDf);
        var t = StudentT.OneSidedCritical(0.05, atFloor.TrendDf);
        Assert.Equal(1.859548, t, 5);
        Assert.True(t > 1.12 * 1.6448536);
    }

    [Fact]
    public void TheVerdictCarriesItsOwnAudit_SampleAndBothCriticalValues()
    {
        // Whatever the flag says, a reader can recompute it: the effective sample and both critical
        // values travel with the verdict (finding 290's print-the-denominator discipline).
        var ic = new List<double>();
        for (var i = 0; i < FiveYears; i++) ic.Add(0.05);

        var v = SignalTrendInference.Infer(ic, 63, FiveYears, 0.05, 0.05);
        Assert.Equal(20, v.Sample.Count);
        Assert.Equal(StudentT.OneSidedCritical(0.05, 19), v.LevelCritical!.Value, 9);
        Assert.Equal(StudentT.OneSidedCritical(0.05, 18), v.TrendCritical!.Value, 9);
    }
}
