using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Gate;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Power;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// D118 — the gate's effect and its MDE are one estimator pair (closes finding 285, corrects finding 345).
/// </summary>
public class PairedEffectTests
{
    private static readonly GateOptions Gate = new();

    /// <summary>Deterministic Gaussian shocks — no `Random` seeded per call site, so a failure here is
    /// reproducible byte for byte.</summary>
    private static double[] Noise(int n, double sigma, int seed)
    {
        var rng = new Random(seed);
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            x[i] = sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        return x;
    }

    /// <summary>
    /// A LOW-BETA strategy with a genuine positive alpha, in a rising market — D26's stated motivation,
    /// constructed. β = 0.3, daily α = +0.0005 (12.6 %/yr), benchmark drift +0.0010/day (~25 %/yr).
    ///
    /// The parameters are chosen so the two definitions land on OPPOSITE SIDES of their own MDEs, not merely
    /// at different magnitudes: `raw_gap = α + (β − 1)·mean(r_b) = 0.0005 − 0.0007 = −0.0002/day`, i.e. about
    /// −5 %/yr, while α itself is +12.6 %/yr. Four years and a small idiosyncratic sigma give the regression
    /// enough power to clear its own (much tighter) MDE, which is the point: the beta-adjusted estimator is
    /// not just differently centred, it is LESS NOISY, because OLS removes the benchmark variation the raw
    /// difference carries as pure noise.
    /// </summary>
    private static (double[] Strat, double[] Bench) LowBetaWithRealAlpha(int n = 1008)
    {
        const double beta = 0.3, dailyAlpha = 0.0005, benchDrift = 0.0010;
        var benchShocks = Noise(n, 0.010, 11);
        var idio = Noise(n, 0.0010, 22);

        var bench = new double[n];
        var strat = new double[n];
        for (var i = 0; i < n; i++)
        {
            bench[i] = benchDrift + benchShocks[i];
            strat[i] = dailyAlpha + beta * bench[i] + idio[i];
        }
        return (strat, bench);
    }

    /// <summary>
    /// **The fixture finding 285 says the calibration could never produce.** Every D64 plant is an overlay on
    /// its own base, so every plant is beta-matched by construction and the defect stays invisible there —
    /// "this finding must not be considered evidenced-against". Here beta VARIES, and the two definitions
    /// disagree about the SIGN of the effect, not merely its size.
    ///
    /// The arithmetic: `raw_gap = α + (β − 1)·mean(r_b)`. With β = 0.3 in a rising market the `(β − 1)` term
    /// is a drag that has nothing to do with skill — precisely D26's *"low-beta strategies otherwise
    /// structurally rigged to lose"*.
    /// </summary>
    [Fact]
    public void FX_PairedEffect_LowBeta_RawGapIsNegativeWhileJensenAlphaIsPositive()
    {
        var (strat, bench) = LowBetaWithRealAlpha();

        var raw = PairedEffect.Compute(strat, bench, PairedEffect.RawGap, 21, Gate)!.Value;
        var jensen = PairedEffect.Compute(strat, bench, PairedEffect.Jensen, 21, Gate)!.Value;

        Assert.True(raw.EffectAnn < 0, $"the raw gap should be dragged negative by the low beta, was {raw.EffectAnn:P2}");
        Assert.True(jensen.EffectAnn > 0, $"Jensen's alpha should be positive, was {jensen.EffectAnn:P2}");
        Assert.Null(raw.Beta);                                    // raw_gap never estimates beta — that IS the defect
        Assert.InRange(jensen.Beta!.Value, 0.25, 0.35);            // …and jensen recovers the planted 0.3

        // The verdicts disagree, which is the whole point: the arena would REFUSE a real edge under the raw
        // gap and admit it under D26's definition.
        var rawVerdict = PromotionGate.Decide(raw.EffectAnn, raw.Mde.MdeAnn, raw.Mde.TDays, Gate.MinTrackDays);
        var jensenVerdict = PromotionGate.Decide(jensen.EffectAnn, jensen.Mde.MdeAnn, jensen.Mde.TDays, Gate.MinTrackDays);
        Assert.NotEqual(rawVerdict, jensenVerdict);
        Assert.Equal(PromotionVerdict.Promoted, jensenVerdict);

        // …and the corrected estimator is also TIGHTER: OLS removes the benchmark variation that the raw
        // difference carries as pure noise, so the MDE the effect is judged against shrinks. That is why the
        // v1.9.73 measurement (Jensen's alpha against the RAW-GAP MDE) was a lower bound — finding 345.
        Assert.True(jensen.Mde.MdeAnn < raw.Mde.MdeAnn,
            $"beta-adjustment should tighten the MDE: jensen {jensen.Mde.MdeAnn:P2} vs raw {raw.Mde.MdeAnn:P2}");
    }

    /// <summary>
    /// The invariant three downstream consumers rely on: `mde_ann` must be reproducible from the PERSISTED
    /// `sigma_lr` by `MdeCalculator`'s own formula. `AllocationStep` inverts it for the shrinkage SE
    /// (`se = mde/zsum`) and `DetectabilityGate`'s analytic floor takes the median `sigma_lr` — so if the
    /// persisted σ and the persisted MDE ever stop implying each other, one of them is judging a different
    /// quantity and nothing says so.
    /// </summary>
    [Fact]
    public void FX_PairedEffect_PersistedSigmaAndMde_ImplyEachOther()
    {
        var (strat, bench) = LowBetaWithRealAlpha();

        foreach (var definition in new[] { PairedEffect.RawGap, PairedEffect.Jensen })
        {
            var p = PairedEffect.Compute(strat, bench, definition, 21, Gate)!.Value;
            var reconstructed = MdeCalculator.ZSum(Gate.Confidence, Gate.Power)
                                * p.Mde.SigmaLr * MetricsConstants.TradingDaysPerYear / Math.Sqrt(p.Mde.TDays);
            Assert.Equal(p.Mde.MdeAnn, reconstructed, 12);
        }
    }

    /// <summary>
    /// A benchmark with NO variation leaves β unidentified — every (α, β) with `α + β·c` equal to the mean
    /// fits identically. The pair falls back to the β = 1 convention, which is the raw-gap pair: effect AND
    /// MDE together, never a Jensen effect against a raw-gap MDE (that mismatch is finding 345). Matches
    /// `OverfittingMonitor.SafeAlpha`, which has used the same convention since Phase 3.
    ///
    /// `Beta: null` is how a caller can tell no beta adjustment was applied.
    /// </summary>
    [Fact]
    public void FX_PairedEffect_DegenerateBenchmark_FallsBackToTheRawGapPAIR_NotToAMismatch()
    {
        var bench = new double[100];                       // flat: zero variance, beta unidentified
        var strat = Enumerable.Repeat(0.001, 100).ToArray();

        var jensen = PairedEffect.Compute(strat, bench, PairedEffect.Jensen, 21, Gate)!.Value;
        var raw = PairedEffect.Compute(strat, bench, PairedEffect.RawGap, 21, Gate)!.Value;

        Assert.Equal(raw.EffectAnn, jensen.EffectAnn, 12);
        Assert.Equal(raw.Mde.MdeAnn, jensen.Mde.MdeAnn, 12);
        Assert.Equal(raw.Mde.SigmaLr, jensen.Mde.SigmaLr, 12);
        Assert.Null(jensen.Beta);                          // …and it SAYS no beta adjustment happened
    }

    /// <summary>Unusable series are a SKIP, never a zero — the gate must not hand a verdict to a pair it
    /// cannot evaluate (rule 10). An unknown definition is a programming error and fails loudly.</summary>
    [Fact]
    public void FX_PairedEffect_UnusableSeries_Skip_AndAnUnknownDefinitionThrows()
    {
        Assert.Null(PairedEffect.Compute([0.01], [0.01], PairedEffect.Jensen, 21, Gate));       // < 2 points
        Assert.Null(PairedEffect.Compute([0.01, 0.02], [0.01], PairedEffect.Jensen, 21, Gate)); // mismatched
        Assert.Throws<ArgumentException>(() =>
            PairedEffect.Compute([0.01, 0.02], [0.01, 0.02], "vibes", 21, Gate));
    }
}
