using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// The D41 risk-free series and the excess-at-source convention (checkpoint 6.6).
///
/// **THE LOAD-BEARING FIXTURE IS THE CANCELLATION ONE.** The gate, the D31 paired test and the D48 MDE
/// all depend on RF being irrelevant to a paired DIFFERENCE, and that is usually asserted in prose. Here
/// it is arithmetic: d_t = (r_s − rf_t) − (r_b − rf_t) = r_s − r_b for ANY rf, including a wildly varying
/// one. If subtraction were ever applied asymmetrically — the realistic mistake, since the subject and
/// its population members are excess-adjusted in two different methods — that fixture is what fails.
/// </summary>
public class RiskFreeSeriesTests
{
    private static RiskFreeWindow Window(params double[] rates) => new(rates, rates.Length, rates.Length);

    [Fact]
    public void FR13_D41_Excess_SubtractsPositionally()
    {
        var excess = RiskFreeSeries.Excess([0.010, 0.020, 0.030], Window(0.001, 0.002, 0.003));
        Assert.Equal([0.009, 0.018, 0.027], excess.Select(v => Math.Round(v, 12)));
    }

    /// <summary>A length mismatch is the mistake that produces a PLAUSIBLE wrong answer — every rate
    /// shifted by the offset — so it throws rather than truncating.</summary>
    [Fact]
    public void FR13_D41_AMisalignedWindow_IsRefused_NotTruncated()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => RiskFreeSeries.Excess([0.01, 0.02, 0.03], Window(0.001, 0.002)));
        Assert.Contains("Positional alignment is the contract", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE GATE'S GUARANTEE, as arithmetic rather than as a comment: d_t = (r_s − rf_t) − (r_b − rf_t)
    /// = r_s − r_b for ANY rf. So no risk-free change can move the D31 paired test, the D48 MDE or the
    /// promotion gate's head-to-head.
    ///
    /// **THE CANCELLATION IS ALGEBRAIC, NOT BITWISE, and the first draft of this fixture asserted the
    /// wrong one.** At 15 decimal places it FAILED: subtracting an rf far larger than the returns and
    /// re-adding it loses low bits, leaving a residue of ~2.5E-17 (measured: −0.0030112386415764884 vs
    /// −0.0030112386415765136). That is double rounding proportional to |rf|, not a defect — but
    /// "invariant" and "bit-identical" are different claims and only one of them is true, so the bound is
    /// stated as a number.
    ///
    /// **AND THE STRUCTURAL FACT IS STRONGER THAN THIS TEST ANYWAY:** the gate never sees an excess
    /// series at all. `EvaluationStep` calls `CurveMath.AlignedReturns` (raw) and `PairedEffect` /
    /// `MdeCalculator` reference neither `Excess` nor `RiskFreeSeries`, so the residue below cannot reach
    /// the promotion path even in principle. This fixture guards the property for any FUTURE caller that
    /// does route a paired difference through excess returns.
    /// </summary>
    [Fact]
    public void FR13_D41_ThePairedDifference_IsInvariantToAnyRiskFreeSeries_ToDoublePrecision()
    {
        var rng = new Random(4);
        var strat = new double[500];
        var bench = new double[500];
        var wild = new double[500];
        for (var i = 0; i < 500; i++)
        {
            strat[i] = (rng.NextDouble() - 0.5) * 0.02;
            bench[i] = (rng.NextDouble() - 0.5) * 0.02;
            wild[i] = (rng.NextDouble() - 0.5) * 0.5;   // deliberately absurd, to make the point general
        }
        var rf = new RiskFreeWindow(wild, 500, 500);

        var es = RiskFreeSeries.Excess(strat, rf);
        var eb = RiskFreeSeries.Excess(bench, rf);

        // 1E-15 is ~40× the observed residue and still ~12 orders below any threshold in the system, so
        // it separates "double rounding" from "the subtraction was applied asymmetrically" — which is the
        // realistic defect, since the subject and its population members are excess-adjusted in two
        // different methods and would diverge by the SIZE OF RF, not by 1E-17.
        var worst = 0.0;
        for (var i = 0; i < 500; i++) worst = Math.Max(worst, Math.Abs((strat[i] - bench[i]) - (es[i] - eb[i])));

        Assert.True(worst < 1e-15, $"paired difference moved by {worst:E3}, far above double rounding");
    }

    /// <summary>Sharpe does NOT cancel RF — it is an absolute excess-return statistic. Pinned because it
    /// is the counterexample to the old MetricsConstants note's claim that RF "never enters a verdict":
    /// S2 is computed from Sharpe.</summary>
    [Fact]
    public void FR13_D41_Sharpe_DoesNotCancelRiskFree_UnlikeThePairedDifference()
    {
        var rng = new Random(9);
        var returns = new double[500];
        for (var i = 0; i < 500; i++) returns[i] = 0.0004 + (rng.NextDouble() - 0.5) * 0.01;

        var flat = new double[500];
        Array.Fill(flat, 0.0002);   // ~5%/yr

        var atZero = StrategyMetrics.Sharpe(returns, 0.0);
        var atRealRf = StrategyMetrics.Sharpe(RiskFreeSeries.Excess(returns, new RiskFreeWindow(flat, 500, 500)), 0.0);

        Assert.True(atRealRf < atZero,
            $"a positive risk-free rate must LOWER Sharpe; got {atRealRf:F4} against {atZero:F4}");
    }

    /// <summary>α moves too, but only through β ≠ 1 — the `PairedEffect.cs` note, as a fixture. At β ≈ 1
    /// the shift is negligible; away from it, it is not.</summary>
    [Fact]
    public void FR13_D41_JensenAlpha_MovesWithRiskFree_OnlyThroughBetaAwayFromOne()
    {
        var rng = new Random(17);
        var bench = new double[600];
        var leveraged = new double[600];
        var unit = new double[600];
        for (var i = 0; i < 600; i++)
        {
            bench[i] = (rng.NextDouble() - 0.5) * 0.02;
            leveraged[i] = 2.0 * bench[i] + (rng.NextDouble() - 0.5) * 0.001;   // β ≈ 2
            unit[i] = 1.0 * bench[i] + (rng.NextDouble() - 0.5) * 0.001;        // β ≈ 1
        }
        var flat = new double[600];
        Array.Fill(flat, 0.0002);
        var rf = new RiskFreeWindow(flat, 600, 600);

        double Shift(double[] strat) =>
            Math.Abs(StrategyMetrics.JensenAlpha(RiskFreeSeries.Excess(strat, rf), RiskFreeSeries.Excess(bench, rf), 0.0, 21).AlphaAnnualized
                     - StrategyMetrics.JensenAlpha(strat, bench, 0.0, 21).AlphaAnnualized);

        Assert.True(Shift(unit) < 1e-3, $"β≈1 should barely move; moved {Shift(unit):E3}");
        Assert.True(Shift(leveraged) > Shift(unit) * 10,
            $"β≈2 must move much more than β≈1; got {Shift(leveraged):E3} vs {Shift(unit):E3}");
    }

    [Fact]
    public void FR13_D41_AnEmptySeries_YieldsZeroRatesAndZeroCoverage()
    {
        var w = RiskFreeSeries.Empty().For(["2026-07-01", "2026-07-02"]);

        Assert.Equal(2, w.Total);
        Assert.Equal(0, w.Covered);
        Assert.False(w.FullyCovered);
        Assert.All(w.Daily, r => Assert.Equal(0.0, r));
    }

    /// <summary>A PARTIALLY covered window is the case rule 10 cares about: the arithmetic proceeds (the
    /// zeros are the pre-D41 behaviour and no worse than it) but coverage says so, and it is that flag —
    /// not the number — that the honesty channel reads before presenting an absolute figure.</summary>
    [Fact]
    public void FR13_D41_PartialCoverage_IsReported_NotHidden()
    {
        var w = RiskFreeSeries.Empty().For(["2026-07-01"]);
        Assert.False(w.FullyCovered);

        var full = new RiskFreeWindow([0.0001, 0.0001], 2, 2);
        Assert.True(full.FullyCovered);

        var partial = new RiskFreeWindow([0.0001, 0.0], 1, 2);
        Assert.False(partial.FullyCovered);
    }
}
