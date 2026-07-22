using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Candidates;

/// <summary>The refusal's structured payload (rendered into the D60 error envelope's details). All
/// effects are annualized FRACTIONS (the power_reports convention — 0.02 = 2%/yr).</summary>
public sealed record DetectabilityDetails(
    double ExpectedEffectAnn,
    double FloorAnn,
    double? AnalyticMdeAnn,
    double? EmpiricalAlphaStarAnn,
    int HorizonYears,
    int TrialsAfterAdmission,
    string SigmaSource);

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
    public DetectabilityVerdict Assess(double expectedEffectAnn)
    {
        var horizonSessions = (int)(Math.Max(1, gate.DetectabilityHorizonYears) * MetricsConstants.TradingDaysPerYear);
        var trialsAfter = db.TrialsRegistry.Count(t => t.RunKind == "live") + 1;

        var (sigma, sigmaSource) = ResolveSigma();
        if (sigma is null)
        {
            return new DetectabilityVerdict(true, "unassessed_no_sigma", null);
        }

        var analytic = BonferroniZSum(trialsAfter) * sigma.Value
                       * MetricsConstants.TradingDaysPerYear / Math.Sqrt(horizonSessions);
        var empirical = EmpiricalAlphaStar(horizonSessions);
        var floor = Math.Max(analytic, empirical ?? 0.0);

        var details = new DetectabilityDetails(
            expectedEffectAnn, floor, analytic, empirical,
            gate.DetectabilityHorizonYears, trialsAfter, sigmaSource);

        if (expectedEffectAnn < floor)
        {
            throw new DetectabilityRefusedException(
                $"Detectability refused (FR-40/D89): the pre-registered expected effect " +
                $"{expectedEffectAnn:P2}/yr could not clear the detection floor {floor:P2}/yr within " +
                $"{gate.DetectabilityHorizonYears} year(s) (analytic NW-MDE {analytic:P2} at N'={trialsAfter} trials" +
                (empirical is { } e ? $"; empirical C-1 floor {(double.IsPositiveInfinity(e) ? "unreachable" : e.ToString("P2"))}" : "; no C-1 curves — analytic only") +
                "). Running it would spend the trials budget on a claim the arena cannot adjudicate.",
                details);
        }
        return new DetectabilityVerdict(true, empirical is null ? "analytic_only" : "admitted", details);
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

    // α*(H) from the frozen Calibration.DetectionPower row (ResolveCurrent — admission is an
    // operational act, not a run-scoped read). Returns an annualized FRACTION; null = no row frozen.
    private double? EmpiricalAlphaStar(int horizonSessions)
    {
        var json = new ConfigReadService(db).ResolveCurrent(CalibratedKeys.DetectionPower);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("curves", out var curves)) return null;

        var levels = new List<(double AlphaPct, double PromotedAtH)>();
        foreach (var property in curves.EnumerateObject())
        {
            if (!double.TryParse(property.Name, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var alphaPct)) continue;
            var p = InterpolatePromoted(property.Value, horizonSessions);
            levels.Add((alphaPct, p));
        }
        if (levels.Count == 0) return null;
        levels.Sort((a, b) => a.AlphaPct.CompareTo(b.AlphaPct));

        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i].PromotedAtH < gate.Power) continue;
            if (i == 0) return levels[0].AlphaPct / 100.0;   // the lowest swept level already clears
            var (lo, hi) = (levels[i - 1], levels[i]);
            var w = (gate.Power - lo.PromotedAtH) / (hi.PromotedAtH - lo.PromotedAtH);
            return (lo.AlphaPct + w * (hi.AlphaPct - lo.AlphaPct)) / 100.0;
        }
        return double.PositiveInfinity;   // no swept level reaches the power at H — nothing is detectable
    }

    private static double InterpolatePromoted(JsonElement curve, int t)
    {
        if (!curve.TryGetProperty("knots", out var knots)) return 0;
        (int T, double P)? prev = null;
        foreach (var k in knots.EnumerateArray())
        {
            var kt = k.GetProperty("t").GetInt32();
            var kp = k.GetProperty("p_promoted").GetDouble();
            if (t <= kt)
            {
                if (prev is not { } pr || kt == pr.T) return kp;
                var w = (t - pr.T) / (double)(kt - pr.T);
                return pr.P + w * (kp - pr.P);
            }
            prev = (kt, kp);
        }
        return prev?.P ?? 0;   // beyond the last knot: flat
    }
}
