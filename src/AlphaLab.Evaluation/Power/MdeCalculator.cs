using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Power;

/// <summary>The NW-corrected minimum-detectable-effect for one A−B pair at one evaluation (D48). Maps
/// 1:1 to a power_reports row (t_days, sigma_lr, nw_lag, mde_ann).</summary>
public readonly record struct MdeResult(int TDays, double SigmaLr, int NwLag, double MdeAnn)
{
    /// <summary>True iff an observed annualized gap is inside the MDE — the gate's TooEarly condition.</summary>
    public bool IsInside(double observedGapAnn) => Math.Abs(observedGapAnn) < MdeAnn;
}

/// <summary>
/// The Newey–West-corrected MDE (DESIGN_IMPROVEMENTS §1.2 / MONITOR Appendix C, D48). From the paired
/// daily active-return difference series d_t = a_t^A − a_t^B:
///
///   σ²_LR = γ₀ + 2·Σ_{k=1..L}(1 − k/(L+1))·γ_k        (Bartlett; via <see cref="NeweyWest"/>)
///   L     = min(2·max(horizon_A, horizon_B), NwLagCapDays)
///   MDE_ann = (z_{1−α/2} + z_power)·σ_LR·252/√T        (= 2.8·σ_LR·252/√T at 95%/80%)
///
/// The AR(1) point (D48): because σ²_LR captures positive autocorrelation, an autocorrelated d_t yields
/// a LARGER MDE than its i.i.d. variance would imply — the honesty metric refuses to over-claim.
/// </summary>
public static class MdeCalculator
{
    /// <summary>z_{1−α/2} + z_power for the configured confidence/power (≈ 2.8016 at 0.95 / 0.80).</summary>
    public static double ZSum(double confidence, double power) =>
        Normal.InvCdf(1.0 - (1.0 - confidence) / 2.0) + Normal.InvCdf(power);

    /// <summary>Compute the MDE for a difference series. <paramref name="maxHorizonDays"/> is the larger
    /// of the two strategies' holding horizons (≥ 1). A series shorter than two observations yields +∞ —
    /// σ_LR is unestimable from fewer than two daily differences, so nothing is detectable yet and the
    /// gate reads TooEarly. (A constant MULTI-point series legitimately yields MDE 0: a difference with
    /// zero variance is decisively distinguishable, and that is correct.)</summary>
    public static MdeResult Compute(IReadOnlyList<double> dailyDiff, int maxHorizonDays, GateOptions gate)
    {
        var t = dailyDiff.Count;
        var lag = Math.Min(2 * Math.Max(maxHorizonDays, 1), gate.NwLagCapDays);
        var sigmaLr = Math.Sqrt(NeweyWest.LongRunVariance(dailyDiff, lag));
        var mde = t >= 2
            ? ZSum(gate.Confidence, gate.Power) * sigmaLr * MetricsConstants.TradingDaysPerYear / Math.Sqrt(t)
            : double.PositiveInfinity;
        return new MdeResult(t, sigmaLr, lag, mde);
    }
}
