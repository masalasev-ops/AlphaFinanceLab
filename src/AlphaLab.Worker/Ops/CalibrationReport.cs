using System.Globalization;
using System.Text;
using AlphaLab.Evaluation.Calibration;
using static System.FormattableString;

namespace AlphaLab.Worker.Ops;

/// <summary>Everything the archived report renders (assembled by <see cref="CalibrationOrchestrator"/>).</summary>
public sealed record CalibrationReportInputs(
    string ArenaId,
    string WindowFrom,
    string WindowTo,
    string Watermark,
    long? FirstRunId,
    long? LastRunId,
    string? LearnThrough,
    string MembershipSource,
    int SeedsPerPlant,
    int PopulationM,
    S3Curve PEdge,
    S3Curve PNoise,
    S3Curve? NaiveEdge,
    double? SensitivityMaxGapPts,
    double SensitivityThresholdPts,
    IReadOnlyDictionary<double, DetectionPowerCurve> DetectionPower,
    ReplayVerificationReport Verification,
    IReadOnlyList<string> FrozenConfigKeys,
    string BuildConfiguration);

/// <summary>One C-1 detection-power curve: P(promoted by t | α) on the eval grid + the median.</summary>
public sealed record DetectionPowerCurve(
    IReadOnlyList<CurveKnot> PromotedByT, double? MedianSessionsToPromotion, int Seeds);

/// <summary>
/// The archived Phase-4 threshold-calibration report (MASTER §20.9; DESIGN_IMPROVEMENTS §5 job 2):
/// markdown, written to docs/calibration/{arena}/. The plant-sensitivity section is PERMANENT (D64);
/// the C-2 sampling band and the per-signal false-alarm table ride along; the data-vintage section
/// carries the survivorship + slice caveats verbatim.
/// </summary>
public static class CalibrationReport
{
    public static string Render(CalibrationReportInputs r, string generatedAtIso)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Threshold-calibration report — arena {r.ArenaId}");
        sb.AppendLine();
        sb.AppendLine($"- Window: **{r.WindowFrom} .. {r.WindowTo}**  (learn through: {r.LearnThrough ?? "(whole window — no partition)"})");
        sb.AppendLine($"- Frozen replay watermark (D95): `{r.Watermark}`");
        sb.AppendLine($"- Replay run span: {(r.FirstRunId is null ? "(none)" : $"run {r.FirstRunId}..{r.LastRunId}")}");
        sb.AppendLine($"- Seeds per plant: {r.SeedsPerPlant} · population M: {r.PopulationM}");
        sb.AppendLine($"- Generated: {generatedAtIso}");
        sb.AppendLine($"- Build configuration: **{r.BuildConfiguration}** (finding 278: the sign-off artifact records which build produced these numbers)");
        sb.AppendLine($"- Config rows frozen this run: {(r.FrozenConfigKeys.Count == 0 ? "(none — report-only, or a verification failure blocked the freeze)" : string.Join(", ", r.FrozenConfigKeys))}");
        sb.AppendLine();

        sb.AppendLine("## D56 trajectory curves (S3)");
        sb.AppendLine();
        Curve(sb, "P_edge(t) — realistic plant (the calibration plant, D64)", r.PEdge);
        Curve(sb, "P_noise(t) — false-alarm envelope of the no-edge plants", r.PNoise);
        sb.AppendLine($"C-2 sampling band: the anchors ride an M={r.PopulationM} empirical distribution — " +
                      $"±{r.PEdge.SamplingBandMembers} members (edge) / ±{r.PNoise.SamplingBandMembers} members (noise) " +
                      "of binomial noise at the defining quantiles. Archived so a future \"should M be 500?\" has its evidence.");
        sb.AppendLine();

        sb.AppendLine("## Plant sensitivity — naive vs realistic (PERMANENT section, D64/FX-PlantRealism)");
        sb.AppendLine();
        if (r.NaiveEdge is null)
        {
            sb.AppendLine("Naive comparator paths unavailable (no naive cohort in this generation).");
        }
        else
        {
            Curve(sb, "P_edge(t) — NAIVE constant-drift comparator (prohibited as the calibration plant)", r.NaiveEdge);
            // Invariant($"…"): the report is archived + SHA-256-hashed into Calibration.ReportRef, so
            // its bytes must not depend on the operator's locale (Phase-4 review).
            sb.AppendLine(r.SensitivityMaxGapPts is { } gap
                ? Invariant($"Max |realistic − naive| divergence at t ≥ 126d: **{gap:F1} percentile points** ") +
                  Invariant($"(threshold {r.SensitivityThresholdPts:F0}). The REALISTIC curves are the frozen ones — always; ") +
                  (gap > r.SensitivityThresholdPts
                      ? "the divergence EXCEEDS the threshold, confirming constant drift would have mis-calibrated the monitor."
                      : "the divergence is inside the threshold at this horizon; the realistic plant remains the calibration plant by construction.")
                : "No knots at t ≥ 126d in this window — divergence not evaluable at this horizon (recorded, not skipped).");
        }
        sb.AppendLine();

        sb.AppendLine("## C-1 detection power — empirical P(promoted by t | α)");
        sb.AppendLine();
        sb.AppendLine("The FR-40 detectability-at-admission gate's empirical floor (D89): swept across the edge-plant");
        sb.AppendLine("alpha levels on the same seeds, validating the analytic NW-MDE end-to-end against the machinery.");
        sb.AppendLine();
        foreach (var (alpha, curve) in r.DetectionPower.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"### α = {alpha.ToString("0.##", CultureInfo.InvariantCulture)}%/yr ({curve.Seeds} seeds)");
            sb.AppendLine();
            sb.AppendLine("| t (sessions) | P(promoted by t) |");
            sb.AppendLine("|---|---|");
            foreach (var k in curve.PromotedByT) sb.AppendLine($"| {k.T} | {k.P.ToString("0.00", CultureInfo.InvariantCulture)} |");
            sb.AppendLine();
            sb.AppendLine($"Median sessions to promotion: {(curve.MedianSessionsToPromotion is { } m ? m.ToString("F0", CultureInfo.InvariantCulture) : "(none promoted)")}");
            sb.AppendLine();
        }

        sb.AppendLine("## Machinery verification + KPIs (FX-Replay15y)");
        sb.AppendLine();
        sb.AppendLine("| Check | Outcome | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (var c in r.Verification.Checks)
        {
            sb.AppendLine($"| {c.Name} | **{c.Outcome}** | {c.Detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }
        sb.AppendLine();
        var k2 = r.Verification.Kpis;
        sb.AppendLine($"- Anti-predictive detection speed (D63): {Fmt(k2.AntiDetectionSpeedMedianSessions, "F0")} sessions (median)");
        sb.AppendLine($"- Days to IndistinguishableFromRandom (D63): {Fmt(k2.DaysToIndistinguishabilityMedian, "F0")}");
        sb.AppendLine($"- Would-be edge-plant survival (from the retire log): 5y {Fmt(k2.WouldBeEdgeSurvival5y, "P0")} · 10y {Fmt(k2.WouldBeEdgeSurvival10y, "P0")}");
        sb.AppendLine($"- Joint any-signal false alarm (monitor flagging — see comparability note): {Fmt(k2.JointFalseAlarmFrac, "P1")}");
        sb.AppendLine($"- No-edge P_noise breach rate, point-level (validate segment): {Fmt(k2.NoEdgeBreachRateValidate, "P1")}");
        sb.AppendLine($"- No-edge curve breach, per-plant sustained (validate — INDEPENDENT of monitor flagging): {Fmt(k2.NoEdgeCurveBreachValidate, "P1")}");
        sb.AppendLine($"- Curve-based edge survival (validate): {Fmt(k2.CurveBasedEdgeSurvival, "P0")}");
        if (k2.ValueAdd is { } va)
        {
            sb.AppendLine(Invariant($"- Allocator value-add (§1.2): gap {va.GapAnn:P2}/yr, MDE {va.MdeAnn:P2}, {va.Verdict}, T={va.TDays}; ") +
                          Invariant($"mean weight edge {va.MeanEdgeWeightPct:F1}% vs anti {va.MeanAntiWeightPct:F1}%"));
        }
        sb.AppendLine();
        sb.AppendLine("### Per-signal false-alarm contribution (finding 114)");
        sb.AppendLine();
        if (k2.FalseAlarmPerSignal.Count == 0) sb.AppendLine("(no no-edge plant ever reached Suspect)");
        else
        {
            sb.AppendLine("| Signal | Alarmed no-edge plants |");
            sb.AppendLine("|---|---|");
            foreach (var (signal, count) in k2.FalseAlarmPerSignal.OrderBy(kv => kv.Key)) sb.AppendLine($"| {signal} | {count} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Data vintage (D64 stamp)");
        sb.AppendLine();
        sb.AppendLine($"- Membership source: {r.MembershipSource}");
        sb.AppendLine("- Survivorship caveat: pre-launch data carries residual survivorship bias (MASTER §13.4) — replay Sharpe is expected to flatter; the curves are relative separations, which is what the monitor consumes.");
        sb.AppendLine("- Slice caveat: curves are calibrated on S&P 500 as-of membership (D70); the FORWARD universe remains the S&P 100 slice until the post-sign-off widen (rule 22).");
        sb.AppendLine("- Replay is slightly LESS informed than a true historical observer (a declared-but-not-yet-effective action is invisible under the D95 date ceiling) — the conservative direction.");
        return sb.ToString();
    }

    private static void Curve(StringBuilder sb, string title, S3Curve curve)
    {
        sb.AppendLine($"### {title}");
        sb.AppendLine();
        sb.AppendLine("| t (sessions) | percentile | 25–75% band |");
        sb.AppendLine("|---|---|---|");
        for (var i = 0; i < curve.Knots.Count; i++)
        {
            var band = i < curve.Band2575.Count
                ? Invariant($"{curve.Band2575[i].Lo:F1}–{curve.Band2575[i].Hi:F1}")
                : "—";
            sb.AppendLine($"| {curve.Knots[i].T} | {curve.Knots[i].P.ToString("F1", CultureInfo.InvariantCulture)} | {band} |");
        }
        sb.AppendLine();
    }

    private static string Fmt(double? v, string format) =>
        v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : "(not evaluable at this scale)";
}
