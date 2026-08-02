using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Candidates;

/// <summary>The refusal's structured payload (rendered into the D60 error envelope's details). All
/// effects are annualized FRACTIONS (the power_reports convention — 0.02 = 2%/yr).
/// <para><paramref name="CeilingAnn"/>/<paramref name="CeilingState"/> are D116's plausibility ceiling and
/// whether it could bind; <paramref name="Reason"/> is the machine-readable outcome the API maps to an
/// error code — <c>below_floor</c> | <c>above_ceiling</c> | <c>floor_unreachable</c> on refusal,
/// <c>admitted</c> | <c>analytic_only</c> on admission.</para></summary>
public sealed record DetectabilityDetails(
    double ExpectedEffectAnn,
    double FloorAnn,
    double? AnalyticMdeAnn,
    double? EmpiricalAlphaStarAnn,
    int HorizonYears,
    int TrialsAfterAdmission,
    string SigmaSource,
    double? CeilingAnn,
    string CeilingState,
    string Reason);

/// <summary>Thrown by the FR-40 gate on refusal. Subclasses InvalidOperationException so any host that
/// only knows the generic validation shape still treats it as a 422 — the API catches THIS type first
/// to emit the dedicated `detectability_refused` code (D99).</summary>
public sealed class DetectabilityRefusedException(string message, DetectabilityDetails details)
    : InvalidOperationException(message)
{
    public DetectabilityDetails Details { get; } = details;
}

/// <summary>What the gate concluded. <see cref="Reason"/> ∈ admitted | refused | unassessed_no_sigma |
/// analytic_only (degraded — no C-1 curves frozen yet).</summary>
public sealed record DetectabilityVerdict(bool Admitted, string Reason, DetectabilityDetails? Details);

/// <summary>
/// The D89/FR-40 detectability-at-admission gate (MASTER §20.3): before a candidate enters the arena,
/// refuse it if its pre-registered expected effect — NET of the incremental trials-budget cost its own
/// admission adds — could not clear the NW-corrected MDE within <c>Gate.DetectabilityHorizonYears</c>.
///
/// **D116 (v1.9.71) added the OTHER end.** Until then this gate compared in one direction only
/// (<c>expected &lt; floor</c>), so a claim of 400%/yr sailed through while a claim of 0.5%/yr was
/// refused — and the researcher's context pack showed it the floor and no upper bound, leaving the seat's
/// only scale cue pointing up (finding 337). The ceiling is <c>top swept rung × the ladder's own
/// geometric step</c>, read from the same frozen <c>Calibration.DetectionPower</c> row as the floor
/// (<see cref="DetectionCurves"/>): no new constant and no new config key, which is the whole defence —
/// an authored ceiling would be `ForkBudgetPerYear = 6` again (finding 309). It is deliberately LOOSE: it
/// bounds absurdity, not optimism, because the instrument for ordinary over-claiming is D110 calibration
/// skill and a tight ceiling would need a number nobody can derive.
///
/// The floor is max(analytic, empirical):
///  • ANALYTIC — MDE_H = (z_{1−α/(2N′)} + z_power)·σ_LR·252/√H, where N′ = the forward trials count + 1
///    (Bonferroni over the honest trials registry — "one researcher's trial spends everyone's
///    significance", so the candidate is charged for the deflation its own registration causes) and
///    σ_LR = the median long-run sigma of recent pair evaluations (forward first; the replay
///    generation's — the calibration vintage estimate — before any forward evaluation exists).
///  • EMPIRICAL — α*(H): the smallest plant alpha whose archived C-1 curve reaches P(promoted by H) ≥
///    Gate.Power, linearly interpolated between swept levels (D89: the curves ARE the calibration).
///    If even the top swept level never promotes within H, the floor is +∞ — the machinery cannot
///    detect anything at that horizon, and admitting on hope would be fail-open.
///
/// With no σ_LR estimable anywhere (a pre-calibration, pre-forward lab) the gate is UNASSESSED and
/// admits — there is no honest number to refuse against, and blocking all research pre-calibration
/// would be a different failure. The gate acts at ADMISSION only; it never re-gates a live strategy
/// (rule 8). Unregistered candidates bypass under their permanent marking (FR-40).
/// </summary>
public sealed class DetectabilityGate(AlphaLabDbContext db, GateOptions gate)
{
    /// <summary>
    /// The arena's current detectability floor, WITHOUT an expected effect to judge against.
    ///
    /// **The floor is a property of the arena, not of the proposal** — `max(analytic, empirical)` reads
    /// the trials count, σ and the frozen curves, and the expected effect enters only at the comparison.
    /// D113 needs this separately because a proposal is stamped with the floor at ASSESSMENT, when there
    /// may be no expected effect yet (the seat writes an unlocked draft; the operator pre-registers it
    /// later). Returning null is the honest `unassessed_no_sigma` answer, never a zero — a zero floor
    /// would say "anything is detectable", which is the opposite of what is known.
    /// </summary>
    public double? ResolveCurrentFloor() => Floor().Floor;

    public DetectabilityVerdict Assess(double expectedEffectAnn)
    {
        var (floorOrNull, analytic, bounds, trialsAfter, sigmaSource) = Floor();
        if (floorOrNull is not { } floor)
        {
            return new DetectabilityVerdict(true, "unassessed_no_sigma", null);
        }

        var empirical = bounds.AlphaStarAnn;
        // D116's third valve: a ceiling at or below the floor cannot bind without producing an EMPTY
        // admissible band, so it is reported and not applied. Reported rather than dropped because the
        // operator still needs to see both ends to understand why nothing is admissible.
        var ceilingState = bounds.CeilingState == DetectionCurves.CeilingApplied
                           && bounds.CeilingAnn is { } cd && cd <= floor
            ? DetectionCurves.CeilingInert
            : bounds.CeilingState;
        var ceilingBinds = ceilingState == DetectionCurves.CeilingApplied;

        DetectabilityDetails Details(string reason) => new(
            expectedEffectAnn, floor, analytic, empirical,
            gate.DetectabilityHorizonYears, trialsAfter, sigmaSource,
            bounds.CeilingAnn, ceilingState, reason);

        // The floor is unreachable AT ALL: no swept rung reaches Gate.Power within the horizon, so α* is
        // +∞ and EVERY registered candidate refuses. The refusal is unchanged (D89 fails closed — admitting
        // on hope would be fail-open); what changes at v1.9.71 is that it says so instead of printing a
        // formatted infinity and blaming the operator's claim (finding 336). The resolution is
        // RECALIBRATION, not a smaller claim, and nothing about the claim would have helped.
        if (double.IsPositiveInfinity(floor))
        {
            throw new DetectabilityRefusedException(
                $"Detectability refused (FR-40/D89, reason `floor_unreachable`): this arena cannot detect " +
                $"ANY effect within {gate.DetectabilityHorizonYears} year(s) — no swept C-1 rung reaches " +
                $"power {gate.Power:P0}" +
                (bounds.TopRungAnn is { } tr && bounds.TopRungPromotedAtH is { } tp
                    ? $" (the largest simulated edge, {tr:P0}/yr, reaches only P={tp:0.00})"
                    : "") +
                $". The expected effect {expectedEffectAnn:P2}/yr is not what refused this candidate and a " +
                "different claim would not help: the detection floor itself is unreachable. Resolve by " +
                "recalibrating the detection-power curves (or by deciding, under its own decision, that " +
                "the horizon should be longer) — never by lowering the bar to admit something.",
                Details("floor_unreachable"));
        }

        if (expectedEffectAnn < floor)
        {
            throw new DetectabilityRefusedException(
                $"Detectability refused (FR-40/D89): the pre-registered expected effect " +
                $"{expectedEffectAnn:P2}/yr could not clear the detection floor {floor:P2}/yr within " +
                $"{gate.DetectabilityHorizonYears} year(s) (analytic NW-MDE {analytic!.Value:P2} at N'={trialsAfter} trials" +
                (empirical is { } e ? $"; empirical C-1 floor {(double.IsPositiveInfinity(e) ? "unreachable" : e.ToString("P2"))}" : "; no C-1 curves — analytic only") +
                "). Running it would spend the trials budget on a claim the arena cannot adjudicate.",
                Details("below_floor"));
        }

        // D116: the plausibility ceiling. Inclusive at the boundary — a claim EQUAL to one step beyond the
        // largest simulated edge is the last admissible one, not the first refused.
        if (ceilingBinds && bounds.CeilingAnn is { } ceiling && expectedEffectAnn > ceiling)
        {
            throw new DetectabilityRefusedException(
                $"Implausible effect refused (D116): the pre-registered expected effect " +
                $"{expectedEffectAnn:P2}/yr exceeds the plausibility ceiling {ceiling:P2}/yr — one geometric " +
                $"step beyond {bounds.TopRungAnn:P0}/yr, the largest edge this arena has ever simulated. " +
                "A claim above it is outside the world the C-1 calibration models, so the arena has no " +
                "evidence about what its machinery does there. Add a calibration rung at that strength " +
                "before pre-registering the claim.",
                Details("above_ceiling"));
        }

        return new DetectabilityVerdict(
            true,
            empirical is null ? "analytic_only" : "admitted",
            Details(empirical is null ? "analytic_only" : "admitted"));
    }

    /// <summary>The floor and everything it was computed from. ONE arithmetic, two callers — a second
    /// copy is how the stamped floor and the gated floor would silently stop being the same number.
    /// The D116 ceiling rides in <c>Bounds</c> because it comes off the SAME curve read (a separate read
    /// could see a different config version and disagree with its own floor).</summary>
    private (double? Floor, double? Analytic, DetectionCurves.Bounds Bounds, int TrialsAfter, string SigmaSource) Floor()
    {
        var horizonSessions = (int)(Math.Max(1, gate.DetectabilityHorizonYears) * MetricsConstants.TradingDaysPerYear);
        var trialsAfter = db.TrialsRegistry.Count(t => t.RunKind == "live") + 1;

        // α*(H) and the D116 ceiling from the frozen Calibration.DetectionPower row (ResolveCurrent —
        // admission is an operational act, not a run-scoped read).
        var bounds = DetectionCurves.Resolve(
            new ConfigReadService(db).ResolveCurrent(CalibratedKeys.DetectionPower), horizonSessions, gate.Power);

        var (sigma, sigmaSource) = ResolveSigma();
        if (sigma is null) return (null, null, bounds, trialsAfter, sigmaSource);

        var analytic = BonferroniZSum(trialsAfter) * sigma.Value
                       * MetricsConstants.TradingDaysPerYear / Math.Sqrt(horizonSessions);
        return (Math.Max(analytic, bounds.AlphaStarAnn ?? 0.0), analytic, bounds, trialsAfter, sigmaSource);
    }

    // z_{1−α/(2N′)} + z_power — the Bonferroni-haircut z-sum (N′=1 reduces to MdeCalculator.ZSum).
    private double BonferroniZSum(int trialsAfter) =>
        Normal.InvCdf(1.0 - (1.0 - gate.Confidence) / (2.0 * Math.Max(1, trialsAfter)))
        + Normal.InvCdf(gate.Power);

    private (double? Sigma, string Source) ResolveSigma()
    {
        double? Median(string runKind)
        {
            var sigmas = db.PowerReports
                .Where(p => p.RunKind == runKind && p.SigmaLr > 0)
                .OrderByDescending(p => p.AsOf)
                .Select(p => p.SigmaLr)
                .Take(50)
                .ToList();
            if (sigmas.Count == 0) return null;
            sigmas.Sort();
            return sigmas[sigmas.Count / 2];
        }
        if (Median("live") is { } forward) return (forward, "forward_power_reports_median");
        if (Median("replay") is { } replay) return (replay, "replay_calibration_median");
        return (null, "none");
    }

}
