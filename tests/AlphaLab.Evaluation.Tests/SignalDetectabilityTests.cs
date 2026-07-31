using AlphaLab.Evaluation.Numerics;
using AlphaLab.Evaluation.Signals;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// FX-SignalMinDetectableIc (finding 305): the detectability floor published beneath every flag.
///
/// THE DEFECT THIS CLOSES. `gone` is a FAILURE TO REJECT — "the 5y mean is not significantly above
/// zero" — and a failure to reject carries no information without the effect size the test had the
/// power to find. Published alone, `gone` reads identically for a rule that is genuinely dead and for
/// an instrument too blind to see anything, which is the exact confusion the effective-sample printing
/// was added to prevent one level up. Same discipline as D89 publishing an MDE beside a gate refusal.
///
/// THE POWER TERM IS THE WHOLE POINT, and it is why this is a derivation fixture rather than a display
/// test. `LevelCritical * se` is already derivable from what the row carries, and it answers a
/// different question — "what mean would have cleared the bar", a fact about this sample. The MDE
/// convention (D48) asks "what TRUE effect would this test have caught", which carries the power term
/// and is ~43 % larger. Publishing the first under the second's name would be a number that quietly
/// overstates the instrument.
/// </summary>
public class SignalDetectabilityTests
{
    private const int FiveYears = 1260;
    private const double Alpha = 0.05;
    private const double Power = 0.80;

    /// <summary>A flat series: constant IC with a tiny alternating wobble so the NW variance is
    /// positive and finite. Amplitude sets the standard error, hence the floor.</summary>
    private static List<double> Series(double level, double wobble, int n = FiveYears)
    {
        var ic = new List<double>(n);
        for (var i = 0; i < n; i++) ic.Add(level + (i % 2 == 0 ? wobble : -wobble));
        return ic;
    }

    [Fact]
    public void TheFloorIsAnMDE_NotARestatementOfTheCriticalValue()
    {
        // The derivation, asserted on the shipped numbers rather than assumed:
        //     MDIC = (t_{1-alpha, df} + t_{power, df}) * se
        // Both t values at the SAME df as the arm they belong to, and one-sided on the alpha term
        // because both arms are directional (finding 302).
        var v = SignalTrendInference.Infer(Series(0.0004, 0.02), 63, FiveYears, Alpha, Alpha, Power);

        Assert.Equal(20, v.Sample.Count);
        Assert.Equal(19, v.Sample.LevelDf);

        var tAlpha = StudentT.OneSidedCritical(Alpha, 19);          // 1.729...
        var tPower = StudentT.OneSidedCritical(1.0 - Power, 19);    // t_{0.80, 19} = 0.861...
        Assert.Equal((tAlpha + tPower) * v.StdError, v.MinDetectableIc!.Value, 12);

        // And it is STRICTLY LARGER than the critical threshold, by exactly the power term. If these
        // two were ever equal, the power term would have been dropped and the published floor would be
        // understating what the instrument can miss.
        Assert.True(v.MinDetectableIc > v.LevelCritical!.Value * v.StdError);
        Assert.Equal(tPower * v.StdError, v.MinDetectableIc!.Value - v.LevelCritical!.Value * v.StdError, 12);

        // The standard error travels too, so the floor is recomputable rather than merely asserted —
        // the same rule that already makes both critical values ride along with the verdict.
        Assert.True(v.StdError > 0);
    }

    [Fact]
    public void ADeadRuleAndABlindInstrumentBothSayGone_AndTheFLOORIsWhatSeparatesThem()
    {
        // This is the finding in one test. Two signals with the IDENTICAL mean rank-IC — zero — so both
        // are `gone` and neither verdict is in doubt. They differ in one thing only: how noisily that
        // zero was measured. One is a dead rule measured precisely; the other is measured so coarsely
        // that a large real edge would have gone unnoticed, and "gone" says nothing about the rule at
        // all. Identical flags, identical means, identical samples — the ONLY published quantity that
        // separates them is the floor.
        var precise = SignalTrendInference.Infer(Series(0.0, 0.004), 63, FiveYears, Alpha, Alpha, Power);
        var noisy = SignalTrendInference.Infer(Series(0.0, 0.120), 63, FiveYears, Alpha, Alpha, Power);

        Assert.Equal(TrendFlag.Gone, precise.Flag);
        Assert.Equal(TrendFlag.Gone, noisy.Flag);                       // same verdict…
        Assert.Equal(precise.MeanIc, noisy.MeanIc, 12);                 // …same mean…
        Assert.Equal(precise.Sample.Count, noisy.Sample.Count);         // …same sample size…
        Assert.Equal(precise.LevelCritical, noisy.LevelCritical);       // …same critical value.

        // The floors differ by more than an order of magnitude. Without them a reader has no way to
        // know that the second `gone` is a statement about the instrument, not about the rule.
        Assert.True(noisy.MinDetectableIc > 10 * precise.MinDetectableIc,
            $"precise floor {precise.MinDetectableIc}, noisy floor {noisy.MinDetectableIc}");
    }

    [Fact]
    public void AThinnerSamplePublishesAHigherFloor_MonotonicallyInTheHorizon()
    {
        // The floor must move the honest way with the effective sample: k=63 leaves n_eff 20 where k=21
        // leaves 60, so the same underlying noise buys a coarser instrument at the longer horizon. If
        // this ever inverted, the number would be reassuring precisely where the evidence is thinnest.
        var wide = SignalTrendInference.Infer(Series(0.0002, 0.02), 63, FiveYears, Alpha, Alpha, Power);
        var tight = SignalTrendInference.Infer(Series(0.0002, 0.02), 21, FiveYears, Alpha, Alpha, Power);

        Assert.Equal(20, wide.Sample.Count);
        Assert.Equal(60, tight.Sample.Count);
        Assert.True(wide.MinDetectableIc > tight.MinDetectableIc,
            $"k=63 floor {wide.MinDetectableIc} must exceed k=21 floor {tight.MinDetectableIc}");
    }

    [Fact]
    public void StableAlsoCarriesADecayFloor_BecauseItIsAlsoAFailureToReject()
    {
        // "We found no decay" is the same kind of claim as "we found no edge", and deserves the same
        // treatment: the shallowest decay the test could have caught, annualized the way D48 annualizes
        // its alpha MDE.
        var v = SignalTrendInference.Infer(Series(0.05, 0.01), 63, FiveYears, Alpha, Alpha, Power);

        Assert.Equal(TrendFlag.Stable, v.Flag);
        Assert.NotNull(v.MinDetectableTrendPerYear);
        Assert.True(v.MinDetectableTrendPerYear > 0);

        var tAlpha = StudentT.OneSidedCritical(Alpha, 18);        // the TREND arm's df, not the level's
        var tPower = StudentT.OneSidedCritical(1.0 - Power, 18);
        Assert.Equal((tAlpha + tPower) * v.SlopeStdError!.Value * 252.0,
            v.MinDetectableTrendPerYear!.Value, 12);
    }

    [Fact]
    public void UnpinnedPower_WithholdsTheFloorsButNEVERTheVerdict()
    {
        // The asymmetry that keeps this a diagnostic: an absent power withholds a NUMBER, where an
        // absent alpha withholds a VERDICT. Quoting a floor at a power nobody chose would be the same
        // defect as a silently-defaulted significance level; blocking the flag on it would be worse,
        // since no flag depends on power.
        var withPower = SignalTrendInference.Infer(Series(0.05, 0.01), 63, FiveYears, Alpha, Alpha, Power);
        var without = SignalTrendInference.Infer(Series(0.05, 0.01), 63, FiveYears, Alpha, Alpha, power: null);

        Assert.Equal(withPower.Flag, without.Flag);                   // the verdict is untouched…
        Assert.Equal(withPower.TStat, without.TStat);
        Assert.Equal(withPower.LevelCritical, without.LevelCritical);
        Assert.Null(without.MinDetectableIc);                         // …only the diagnostic is withheld
        Assert.Null(without.MinDetectableTrendPerYear);
    }

    [Fact]
    public void BelowTheFloor_NoVerdictAndNoFalsePrecision()
    {
        // Below n_eff = 10 there is no critical value, so there is no floor either. An `insufficient`
        // row must not publish a detectability number: it would imply a test was run.
        var v = SignalTrendInference.Infer(Series(0.05, 0.01, 300), 63, 300, Alpha, Alpha, Power);

        Assert.Equal(TrendFlag.Insufficient, v.Flag);
        Assert.True(v.Sample.Count < 10);
        Assert.Null(v.MinDetectableIc);
        Assert.Null(v.MinDetectableTrendPerYear);
    }
}
