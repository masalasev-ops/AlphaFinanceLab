using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Power;

/// <summary>The effect a pair evaluation judges, and the MDE it is judged against — produced together so
/// they are always the SAME estimator (D118).</summary>
/// <param name="EffectAnn">Annualized: Jensen's α under <c>jensen</c>, the raw active-return gap under
/// <c>raw_gap</c>.</param>
/// <param name="Mde">Maps 1:1 to the `power_reports` columns, so what is persisted is what was judged.</param>
/// <param name="Beta">The fitted β under <c>jensen</c>; null under <c>raw_gap</c>, which assumes β = 1
/// without estimating it — the assumption finding 285 is about.</param>
public readonly record struct PairedEffectResult(double EffectAnn, MdeResult Mde, double? Beta);

/// <summary>
/// **D118 — the effect and its MDE are ONE estimator pair.**
///
/// Before this, `EvaluationStep` formed the effect as `mean(r_s − r_b) × 252` (a raw active-return gap, no
/// beta term — finding 285, against D26 and hard rule 6) and judged it against an MDE built from `σ_LR` of
/// that same β = 1 difference series. Substituting Jensen's α for the effect while leaving that MDE in
/// place — which is what the recompute harness first did — pairs an intercept with the noise of a
/// DIFFERENT estimator and is not a coherent test (finding 345). So the two are produced here, together,
/// from one fit at one lag: there is no call site at which they can drift apart.
///
/// **On <c>sigma_lr</c> under `jensen`.** It is set to <c>AlphaSe × √T</c> — the effective long-run sigma
/// implied by the HAC α standard error. That is not cosmetic. Three downstream formulas already divide a
/// persisted σ by √T or √H: <see cref="MdeCalculator"/>'s own arithmetic, the allocator's shrinkage SE
/// (`AllocationStep` reads `mde/zsum`), and `DetectabilityGate`'s ANALYTIC FLOOR, which takes the median
/// `sigma_lr` over recent `power_reports`. Defining σ this way keeps all three dimensionally correct with
/// no change to any of them, and keeps the detectability floor measuring the noise of the quantity the gate
/// now actually judges on. Any other choice leaves one of the three silently comparing α against the spread
/// of something else.
///
/// **<c>raw_gap</c> is retained deliberately and is NOT dead code.** It reproduces generation 1's arithmetic,
/// bug included, which is what `FX-RecomputeParity` requires of the recompute harness (§25.1: the harness
/// must reproduce the known defect before it can be trusted to evaluate the fix for it). The live gate no
/// longer calls it; the harness's parity arm does, against every stored generation, forever. Deleting it as
/// unused would break parity permanently and irrecoverably.
/// </summary>
public static class PairedEffect
{
    /// <summary>Generation 1's arithmetic: `mean(r_s − r_b) × 252`, β assumed 1 and never estimated.</summary>
    public const string RawGap = "raw_gap";

    /// <summary>D26 / hard rule 6: β-adjusted (Jensen's) α with Newey–West errors.</summary>
    public const string Jensen = "jensen";

    /// <summary>
    /// Compute the effect and its MDE together. Returns null only when the series are unusable outright
    /// (mismatched lengths, or fewer than two observations) — a SKIP, never a zero, exactly as the gate
    /// already skipped a &lt; 2-point track. A DEGENERATE benchmark is handled separately and deliberately;
    /// see the fallback inside.
    /// </summary>
    public static PairedEffectResult? Compute(
        IReadOnlyList<double> stratReturns, IReadOnlyList<double> benchReturns,
        string definition, int maxHorizonDays, GateOptions gate)
    {
        ArgumentNullException.ThrowIfNull(stratReturns);
        ArgumentNullException.ThrowIfNull(benchReturns);
        ArgumentNullException.ThrowIfNull(gate);
        if (stratReturns.Count != benchReturns.Count || stratReturns.Count < 2) return null;

        var lag = Math.Min(2 * Math.Max(maxHorizonDays, 1), gate.NwLagCapDays);

        if (definition == RawGap)
        {
            var d = new double[stratReturns.Count];
            for (var i = 0; i < d.Length; i++) d[i] = stratReturns[i] - benchReturns[i];
            return new PairedEffectResult(
                d.Average() * MetricsConstants.TradingDaysPerYear,
                MdeCalculator.Compute(d, maxHorizonDays, gate),
                Beta: null);
        }

        if (definition != Jensen)
        {
            throw new ArgumentException(
                $"Unknown effect definition '{definition}'. Known: '{RawGap}', '{Jensen}'.", nameof(definition));
        }

        // rfDaily: 0.0 — finding 285's recorded second-order note, unchanged here: no JensenAlpha call site
        // is yet on the French RF series (Phase 6). It cancels in the paired difference and shifts α only
        // through β ≠ 1, so it is a known bias of known sign rather than an unexamined one.
        OlsFit fit;
        try
        {
            fit = NeweyWest.Ols(stratReturns, benchReturns, lag);
        }
        catch (ArgumentException)
        {
            // DEGENERATE DESIGN — the benchmark has no variation (or fewer than three observations), so β is
            // UNIDENTIFIED: every (α, β) with α + β·c equal to the mean fits identically. Fall back to the
            // β = 1 convention, which is exactly the raw-gap pair — effect AND MDE together, so the two stay
            // one estimator (the whole point of D118).
            //
            // This is NOT the rule-10 hazard of silently defaulting a MISSING input: the input is present and
            // its beta adjustment is VACUOUS, and it matches the convention `OverfittingMonitor.SafeAlpha`
            // has used since Phase 3 for the same situation. `Beta: null` in the returned pair is how a
            // caller tells that no beta adjustment was applied.
            //
            // It cannot fire on live data — a real cap-weight benchmark account varies every session — so in
            // practice this is the synthetic-fixture path, and it keeps those fixtures meaningful rather than
            // silently unevaluated.
            return Compute(stratReturns, benchReturns, RawGap, maxHorizonDays, gate);
        }

        var t = stratReturns.Count;
        var sigmaLr = fit.AlphaSe * Math.Sqrt(t);
        var mdeAnn = MdeCalculator.ZSum(gate.Confidence, gate.Power)
                     * fit.AlphaSe * MetricsConstants.TradingDaysPerYear;

        return new PairedEffectResult(
            fit.Alpha * MetricsConstants.TradingDaysPerYear,
            new MdeResult(t, sigmaLr, lag, mdeAnn),
            fit.Beta);
    }
}
