using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Monitor;
using AlphaLab.Evaluation.Power;
using static System.FormattableString;

namespace AlphaLab.Evaluation.Calibration;

/// <summary>Tri-state so a check that CANNOT be evaluated at this scale (a CI-mini window has no 5-year
/// horizon) reads INSUFFICIENT — visibly — rather than a hollow green. The full-scale DoD requires every
/// check to PASS; the CI-mini suite requires none to FAIL.</summary>
public enum CheckOutcome
{
    Pass,
    Fail,
    Insufficient,
}

public sealed record VerificationCheck(string Name, CheckOutcome Outcome, string Detail, double? Value = null);

/// <summary>The §1.2 allocator value-add KPI (v1.9.23 phasing: validated HERE, in replay, against the
/// D64 plants): the D51 blend vs static equal-weight over the same roster, paired with its own NW-MDE.</summary>
public sealed record AllocatorValueAdd(
    double GapAnn, double MdeAnn, int TDays, string Verdict,
    double MeanEdgeWeightPct, double MeanAntiWeightPct);

/// <summary>The recorded D63/finding-113/114 KPI numbers (null = not computable at this scale). The
/// <c>WouldBeEdgeSurvival</c> pair is the retained finding-113 metric read from the would-be-retire log
/// (Change 1/2); <c>NoEdgeCurveBreachValidate</c> / <c>CurveBasedEdgeSurvival</c> are the Change-2
/// out-of-sample curve-based analogues with their own threshold keys.</summary>
public sealed record ReplayKpis(
    double? AntiDetectionSpeedMedianSessions,
    double? DaysToIndistinguishabilityMedian,
    double? WouldBeEdgeSurvival5y,
    double? WouldBeEdgeSurvival10y,
    double? JointFalseAlarmFrac,
    IReadOnlyDictionary<string, int> FalseAlarmPerSignal,
    double? NoEdgeBreachRateValidate,
    double? NoEdgeCurveBreachValidate,
    double? CurveBasedEdgeSurvival,
    AllocatorValueAdd? ValueAdd);

public sealed record ReplayVerificationReport(IReadOnlyList<VerificationCheck> Checks, ReplayKpis Kpis)
{
    /// <summary>The full-scale DoD bar: every check evaluated AND green.</summary>
    public bool AllGreen => Checks.All(c => c.Outcome == CheckOutcome.Pass);

    /// <summary>The CI-mini bar: nothing FAILED (Insufficient is honest, not green).</summary>
    public bool NoFailures => Checks.All(c => c.Outcome != CheckOutcome.Fail);
}

/// <summary>
/// The FX-Replay15y machinery-validation assertions (FR-19/FR-36; MASTER §20.9; findings 113/114),
/// implemented ONCE and consumed by both the CI-mini tests and the full-scale `replay-calibrate` run
/// (whose report archives every number). Reads only the quarantined replay generation.
/// </summary>
public sealed class ReplayVerification(
    AlphaLabDbContext db,
    GateOptions gate,
    VerdictsOptions verdicts,
    ReplayOptions replay,
    PlantOptions plant)
{
    private const string Replay = "replay";
    private const int SessionsPerYear = 252;

    public ReplayVerificationReport Run(
        IReadOnlyList<PlantSpec> specs, string? learnThrough, S3Curve? builtPNoise, S3Curve? builtPEdge = null)
    {
        var checks = new List<VerificationCheck>();

        var ids = (
            edge: PrimaryEdgeIds(specs, plant), floorEdge: FloorEdgeIds(specs),
            noEdge: Ids(specs, PlantKind.NoEdge), anti: Ids(specs, PlantKind.Anti));

        var windowSessions = db.Runs.Count(r => r.RunKind == Replay && r.Status == "ok");
        var anyEvaluations = db.PowerReports.Any(p => p.RunKind == Replay);

        // ---- promotions ≤ chance (the binomial bound on false promotions of genuinely edgeless plants) ----
        double? jointFalseAlarm = null;
        if (!anyEvaluations)
        {
            checks.Add(new VerificationCheck("promotions_le_chance", CheckOutcome.Insufficient, "no replay evaluations ran"));
            checks.Add(new VerificationCheck("edge_plant_detected", CheckOutcome.Insufficient, "no replay evaluations ran"));
            checks.Add(new VerificationCheck("joint_false_alarm", CheckOutcome.Insufficient, "no replay evaluations ran"));
        }
        else
        {
            var promotedNoEdge = PromotedAmong(ids.noEdge);
            var n = ids.noEdge.Count;
            var p = (1.0 - gate.Confidence) / 2.0;   // the one-sided false-promotion rate per plant
            var bound = Math.Ceiling(n * p + 2.0 * Math.Sqrt(Math.Max(1e-12, n * p * (1 - p))));
            // Invariant($"…") on every Detail carrying a formatted number: these strings flow verbatim
            // into the archived, SHA-256-hashed calibration report — locale-independent by contract
            // (Phase-4 review).
            checks.Add(new VerificationCheck("promotions_le_chance",
                promotedNoEdge <= bound ? CheckOutcome.Pass : CheckOutcome.Fail,
                Invariant($"{promotedNoEdge}/{n} no-edge plants promoted; chance bound {bound} at p={p:F4}"), promotedNoEdge));

            // The RULE-SELECTED primary (Change 4) is the detection gate; the per-RUNG promotion is the C-1
            // detection-power curve — the checkpoint's primary finding, carried in the Detail (read this, not
            // the gate colour). Every edge cohort's promotion count, so the reader sees the whole ladder.
            var detectedEdge = PromotedAmong(ids.edge);
            var perRung = specs.Where(s => s.Kind == PlantKind.Edge)
                .GroupBy(s => (s.Family, s.AlphaAnnPct))
                .OrderBy(g => g.Key.Family, StringComparer.Ordinal).ThenBy(g => g.Key.AlphaAnnPct)
                .Select(g => Invariant($"{g.Key.Family}@{g.Key.AlphaAnnPct:0.##}%:{PromotedAmong(g.Select(s => s.StrategyId).ToList())}/{g.Count()}"))
                .ToList();
            checks.Add(new VerificationCheck("edge_plant_detected",
                detectedEdge > 0 ? CheckOutcome.Pass : windowSessions < gate.MinTrackDays ? CheckOutcome.Insufficient : CheckOutcome.Fail,
                Invariant($"{detectedEdge}/{ids.edge.Count} PRIMARY edge plants promoted (window {windowSessions} sessions); ") +
                Invariant($"detection-power by rung — {string.Join(", ", perRung)}"), detectedEdge));

            // ---- joint any-signal false alarm (finding 114): no-edge plants EVER Suspect/Retired ----
            var alarmed = SuspectEver(ids.noEdge);
            jointFalseAlarm = ids.noEdge.Count == 0 ? 0 : alarmed.Count / (double)ids.noEdge.Count;
            checks.Add(new VerificationCheck("joint_false_alarm",
                jointFalseAlarm <= replay.JointFalseAlarmMaxFrac ? CheckOutcome.Pass : CheckOutcome.Fail,
                Invariant($"{alarmed.Count}/{ids.noEdge.Count} no-edge plants ever Suspect (bound {replay.JointFalseAlarmMaxFrac:P0})"),
                jointFalseAlarm));
        }
        var perSignal = FalseAlarmContributions(ids.noEdge);

        // ---- anti-predictive detection speed (D63 KPI) ----
        var antiSpeed = MedianSessionsToFirstSuspect(ids.anti);
        var antiSuspect = SuspectEver(ids.anti);
        checks.Add(antiSpeed is { } speed
            ? new VerificationCheck("anti_detection_speed",
                antiSuspect.Count * 2 >= ids.anti.Count ? CheckOutcome.Pass : CheckOutcome.Fail,
                Invariant($"median {speed:F0} sessions to first Suspect; {antiSuspect.Count}/{ids.anti.Count} anti plants caught"), speed)
            : new VerificationCheck("anti_detection_speed", CheckOutcome.Insufficient, "no anti plant reached Suspect (short window?)"));

        // ---- days-to-indistinguishability (D63 KPI): no-edge plants earn the chip at the honest cadence ----
        double? daysToChip = null;
        if (windowSessions < verdicts.SeparationMinTrackDays)
        {
            checks.Add(new VerificationCheck("days_to_indistinguishability", CheckOutcome.Insufficient,
                $"window {windowSessions} < SeparationMinTrackDays {verdicts.SeparationMinTrackDays}"));
        }
        else
        {
            // A no-edge plant that reaches the minimum track WITHOUT a promotion has earned the chip;
            // the honest cadence IS the threshold (the chip may not render earlier by design).
            var unpromoted = ids.noEdge.Count - PromotedAmong(ids.noEdge);
            daysToChip = verdicts.SeparationMinTrackDays;
            checks.Add(new VerificationCheck("days_to_indistinguishability",
                unpromoted * 2 >= ids.noEdge.Count ? CheckOutcome.Pass : CheckOutcome.Fail,
                $"{unpromoted}/{ids.noEdge.Count} no-edge plants unpromoted at the {verdicts.SeparationMinTrackDays}-day chip threshold",
                daysToChip));
        }

        // ---- no-edge P_noise breach rate on the VALIDATE segment (out-of-sample honesty, FR-42) ----
        double? breachRate = null;
        if (builtPNoise is null || learnThrough is null)
        {
            checks.Add(new VerificationCheck("noedge_pnoise_breach_validate", CheckOutcome.Insufficient,
                builtPNoise is null ? "no built P_noise curve supplied" : "no learn/validate partition set"));
        }
        else
        {
            var points = ValidatePercentilePoints(ids.noEdge, learnThrough);
            if (points.Count == 0)
            {
                checks.Add(new VerificationCheck("noedge_pnoise_breach_validate", CheckOutcome.Insufficient,
                    "no validate-period S3 points"));
            }
            else
            {
                var breaches = points.Count(pt => pt.Pct < builtPNoise.At(pt.TrackDays));
                breachRate = breaches / (double)points.Count;
                checks.Add(new VerificationCheck("noedge_pnoise_breach_validate",
                    breachRate <= 2.0 * builtPNoise.FalseAlarmRate ? CheckOutcome.Pass : CheckOutcome.Fail,
                    Invariant($"{breaches}/{points.Count} validate-period no-edge points below P_noise (target ≈ {builtPNoise.FalseAlarmRate:P0})"),
                    breachRate));
            }
        }

        // ---- Change 2 (Pass 2): the curve-based OUT-OF-SAMPLE metrics — per-PLANT SUSTAINED breaches of the
        // built P_noise on the held-out validate segment (the S3Trajectory sustain logic, not a single-point
        // breach). These are the INDEPENDENT validation of the curves (amendment C1): they validate the
        // CURVES, not the monitor's flat-anchor flagging (which Change 3 altered), so unlike the retained
        // joint_false_alarm they carry their OWN threshold keys and do not move when the monitor is tuned.
        double? noEdgeCurveBreach = null, curveEdgeSurvival = null;
        if (builtPNoise is null || learnThrough is null)
        {
            var why = builtPNoise is null ? "no built P_noise curve supplied" : "no learn/validate partition set";
            checks.Add(new VerificationCheck("noedge_curve_breach_validate", CheckOutcome.Insufficient, why));
            checks.Add(new VerificationCheck("curve_based_edge_survival", CheckOutcome.Insufficient, why));
        }
        else
        {
            // no-edge: fraction of plants that SUSTAIN-breach P_noise on validate (a mid-band no-edge plant
            // should not — it hovers at its median, breaching only at the false-alarm rate, D63).
            var breaching = ids.noEdge.Count(id => SustainsAcross(id, learnThrough, builtPNoise, belowCurve: true));
            noEdgeCurveBreach = ids.noEdge.Count == 0 ? 0.0 : breaching / (double)ids.noEdge.Count;
            checks.Add(new VerificationCheck("noedge_curve_breach_validate",
                noEdgeCurveBreach <= replay.NoEdgeCurveBreachMaxFrac ? CheckOutcome.Pass : CheckOutcome.Fail,
                Invariant($"{breaching}/{ids.noEdge.Count} no-edge plants sustain-breach P_noise on validate (bound {replay.NoEdgeCurveBreachMaxFrac:P0})"),
                noEdgeCurveBreach));

            // floor-edge: fraction that do NOT sustain-breach P_noise on validate (a real edge stays above it).
            // When P_edge is supplied, ALSO report how many sustain-CLEAR P_edge (the distinguishable count) —
            // informational context on the same paths, so the report shows survival and separation together.
            if (ids.floorEdge.Count == 0)
            {
                checks.Add(new VerificationCheck("curve_based_edge_survival", CheckOutcome.Insufficient, "no floor-edge plants"));
            }
            else
            {
                var surviving = ids.floorEdge.Count(id => !SustainsAcross(id, learnThrough, builtPNoise, belowCurve: true));
                curveEdgeSurvival = surviving / (double)ids.floorEdge.Count;
                var distinguishable = builtPEdge is null
                    ? (int?)null
                    : ids.floorEdge.Count(id => SustainsAcross(id, learnThrough, builtPEdge, belowCurve: false));
                checks.Add(new VerificationCheck("curve_based_edge_survival",
                    curveEdgeSurvival >= replay.CurveBasedEdgeSurvivalFloor ? CheckOutcome.Pass : CheckOutcome.Fail,
                    Invariant($"{surviving}/{ids.floorEdge.Count} floor-edge plants do not sustain-breach P_noise on validate (floor {replay.CurveBasedEdgeSurvivalFloor:P0})") +
                    (distinguishable is { } d ? Invariant($"; {d}/{ids.floorEdge.Count} sustain-clear P_edge (distinguishable)") : ""),
                    curveEdgeSurvival));
            }
        }

        // ---- edge-plant survival at 5y/10y (finding 113) + every retire logged with its trigger ----
        // The floor cohort is the MIN-ALPHA D64 edge plants — the daily SURVIVAL plant plus the monthly base
        // rung (Change 4) — never the higher monthly ladder rungs (the C-1 detection-power sweep, easy to
        // survive): pooling the strong-edge sweep plants would hold the fraction above the floor while the
        // small-edge cohort fails it, suppressing exactly the S6-patience recalibration the floor exists to
        // trigger. Daily is deliberately IN here (survival) and OUT of PrimaryEdgeIds (promotion) — the
        // monitor track cancels its cost drag (same-cadence population ⇒ same turnover/cost), unlike the
        // absolute benchmark hurdle on the promotion track.
        // Change 1/2 (amendment A2): edge plants are EXEMPT from actually retiring under a calibration
        // replay, so survival is read from the WOULD-BE retire log (go_live_log 'WouldRevert'), not the
        // 'retired' status — otherwise the exemption would make this trivially 1.00 (the vacuous trap). This
        // retained finding-113 metric keeps its own EdgePlantSurvivalFloor5y key; the curve-based analogue
        // (curve_based_edge_survival, below) is the independent out-of-sample check with its own key.
        var (survival5, survival10) = WouldBeEdgeSurvival(ids.floorEdge, windowSessions);
        checks.Add(survival5 is { } s5
            ? new VerificationCheck("would_be_edge_survival_5y",
                s5 >= replay.EdgePlantSurvivalFloor5y ? CheckOutcome.Pass : CheckOutcome.Fail,
                Invariant($"{s5:P0} of {ids.floorEdge.Count} min-alpha D64 edge plants (daily survival + monthly base; sweep excluded) would-survive 5y ") +
                Invariant($"(floor {replay.EdgePlantSurvivalFloor5y:P0} over n={ids.floorEdge.Count} — read the denominator, 0.90 over a small cohort is noisy; a floor failure recalibrates S6's patience, never the plant)"), s5)
            : new VerificationCheck("would_be_edge_survival_5y", CheckOutcome.Insufficient, Invariant($"window {windowSessions} < 5y")));
        // finding 113 audit: every WOULD-BE edge retire is logged WITH its triggering signal (Change 1
        // writes the 'WouldRevert' row and the s2/s3/s6 contributions atomically). Guards against a
        // regression that records the would-be retire but drops the signal that caused it.
        var wouldRetiredEdges = WouldBeRetiredAmong(ids.floorEdge);
        var unsignedRetires = db.GoLiveLog
            .Where(g => g.RunKind == Replay && g.Verdict == OverfittingMonitor.WouldRevertVerdict
                        && g.Demoted != null && ids.floorEdge.Contains(g.Demoted!))
            .AsEnumerable()
            .Where(g => !g.EvidenceJson.Contains("consecutive_suspect", StringComparison.Ordinal))
            .Select(g => g.Demoted!).Distinct().ToList();
        checks.Add(new VerificationCheck("edge_retires_logged",
            unsignedRetires.Count == 0 ? CheckOutcome.Pass : CheckOutcome.Fail,
            wouldRetiredEdges.Count == 0
                ? "no edge plant would auto-retire (nothing to log)"
                : $"{wouldRetiredEdges.Count} would-be edge retire(s) logged; {unsignedRetires.Count} missing a triggering signal"));

        // ---- the §1.2 allocator value-add KPI (its live read-model belongs to the D58 set) ----
        // Behavioral judgment (overweight edge / shed anti) only once the paired track clears the
        // gate's own minimum — below it the weights are TooEarly-capped near-equal by design and the
        // comparison would be noise wearing a verdict.
        var valueAdd = AllocatorValueAddKpi.Compute(db, gate, specs);
        checks.Add(valueAdd is { } va
            ? new VerificationCheck("allocator_value_add",
                va.TDays < gate.MinTrackDays
                    ? CheckOutcome.Insufficient
                    : va.MeanEdgeWeightPct > va.MeanAntiWeightPct ? CheckOutcome.Pass : CheckOutcome.Fail,
                Invariant($"blend−EW gap {va.GapAnn:P2}/yr (MDE {va.MdeAnn:P2}, {va.Verdict}, T={va.TDays}); ") +
                Invariant($"mean weight edge {va.MeanEdgeWeightPct:F1}% vs anti {va.MeanAntiWeightPct:F1}%"))
            : new VerificationCheck("allocator_value_add", CheckOutcome.Insufficient, "no replay allocations ran"));

        // ---- the FR-39 cohort seam: plant cohorts have persisted S3 paths to reconstruct from (NFR-2) ----
        checks.Add(anyEvaluations
            ? new VerificationCheck("cohort_s3_paths_present",
                db.OverfittingChecks.Any(c => c.RunKind == Replay && c.Signal == "S3") ? CheckOutcome.Pass : CheckOutcome.Fail,
                "persisted replay S3 percentile paths exist for cohort reconstruction")
            : new VerificationCheck("cohort_s3_paths_present", CheckOutcome.Insufficient, "no replay evaluations ran"));

        return new ReplayVerificationReport(checks, new ReplayKpis(
            antiSpeed, daysToChip, survival5, survival10, jointFalseAlarm, perSignal,
            breachRate, noEdgeCurveBreach, curveEdgeSurvival, valueAdd));
    }

    // ---- helpers over the quarantined generation ----

    private static List<string> Ids(IEnumerable<PlantSpec> specs, PlantKind kind) =>
        specs.Where(s => s.Kind == kind).Select(s => s.StrategyId).ToList();

    /// <summary>Change 4 (B3) — the RULE-SELECTED primary edge cohort: per cadence, the smallest ladder rung
    /// that CLEARS that cadence's pre-registered offline cost_drag+MDE floor; the primary cadence is the one
    /// whose clearing rung is smallest (the most efficient detectable edge). A stated rule, not a hand-picked
    /// plant — that is what keeps this from being tuning-by-another-name. Daily's floor is unreachable by any
    /// plausible overlay, so the primary is the smallest MONTHLY rung clearing the monthly floor (the 16%
    /// detection-sanity rung at the defaults). Empty when NO cadence clears its floor at any rung — itself a
    /// recorded finding (the machinery cannot demonstrate an edge at the pre-registered strengths).</summary>
    public static List<string> PrimaryEdgeIds(IReadOnlyList<PlantSpec> specs, PlantOptions plant)
    {
        var edges = specs.Where(s => s.Kind == PlantKind.Edge).ToList();
        if (edges.Count == 0) return [];
        var clearing = edges
            .GroupBy(s => s.Family)
            .Select(g => (Family: g.Key, Rung: g.Select(s => s.AlphaAnnPct)
                .Where(a => a >= plant.MdeFloorFor(g.Key)).DefaultIfEmpty(double.PositiveInfinity).Min()))
            .Where(x => double.IsFinite(x.Rung))
            .OrderBy(x => x.Rung)
            .ToList();
        if (clearing.Count == 0) return [];
        var (family, rung) = clearing[0];
        return edges.Where(s => s.Family == family && s.AlphaAnnPct == rung).Select(s => s.StrategyId).ToList();
    }

    /// <summary>The finding-113 survival-floor cohort: every EDGE plant at the PRIMARY alpha — the
    /// daily cohort plus the finding-199 monthly (low-turnover) cohort — and never the 2x/4x C-1
    /// sweep levels, which exist only as detection-power inputs (D89) and would dilute the floor in
    /// the easy direction.</summary>
    public static List<string> FloorEdgeIds(IReadOnlyList<PlantSpec> specs)
    {
        var edges = specs.Where(s => s.Kind == PlantKind.Edge).ToList();
        if (edges.Count == 0) return [];
        var primaryAlpha = edges.Min(s => s.AlphaAnnPct);
        return edges.Where(s => s.AlphaAnnPct == primaryAlpha).Select(s => s.StrategyId).ToList();
    }

    private int PromotedAmong(IReadOnlyCollection<string> ids) =>
        db.GoLiveLog.Where(g => g.RunKind == Replay && g.Promoted != null && ids.Contains(g.Promoted!))
            .Select(g => g.Promoted!).Distinct().Count();

    private List<string> SuspectEver(IReadOnlyCollection<string> ids) =>
        db.OverfittingStatus
            .Where(o => o.RunKind == Replay && (o.Status == "suspect" || o.Status == "retired") && ids.Contains(o.StrategyId))
            .Select(o => o.StrategyId).Distinct().ToList();

    /// <summary>The floor-edge plants that WOULD have auto-retired under the calibration replay — read from
    /// the go_live_log 'WouldRevert' rows Change 1 records in lieu of an actual retire (never the 'retired'
    /// status, which the exemption suppresses).</summary>
    private List<string> WouldBeRetiredAmong(IReadOnlyCollection<string> ids) =>
        db.GoLiveLog
            .Where(g => g.RunKind == Replay && g.Verdict == OverfittingMonitor.WouldRevertVerdict
                        && g.Demoted != null && ids.Contains(g.Demoted!))
            .Select(g => g.Demoted!).Distinct().ToList();

    private double? MedianSessionsToFirstSuspect(IReadOnlyCollection<string> ids)
    {
        var windowStart = db.Runs.Where(r => r.RunKind == Replay && r.Status == "ok").Min(r => (string?)r.AsOf);
        if (windowStart is null) return null;
        var firsts = new List<double>();
        foreach (var id in ids)
        {
            var first = db.OverfittingStatus
                .Where(o => o.RunKind == Replay && o.StrategyId == id && (o.Status == "suspect" || o.Status == "retired"))
                .Min(o => (string?)o.AsOf);
            if (first is null) continue;
            firsts.Add(db.Runs.Count(r => r.RunKind == Replay && r.Status == "ok" && string.Compare(r.AsOf, first) <= 0));
        }
        if (firsts.Count == 0) return null;
        firsts.Sort();
        return firsts[firsts.Count / 2];
    }

    private (double? At5y, double? At10y) WouldBeEdgeSurvival(IReadOnlyCollection<string> edgeIds, int windowSessions)
    {
        double? At(int years)
        {
            if (windowSessions < years * SessionsPerYear || edgeIds.Count == 0) return null;
            var horizon = SessionAsOf(years * SessionsPerYear);
            if (horizon is null) return null;
            var wouldRetiredBy = db.GoLiveLog
                .Where(g => g.RunKind == Replay && g.Verdict == OverfittingMonitor.WouldRevertVerdict
                            && g.Demoted != null && edgeIds.Contains(g.Demoted!)
                            && string.Compare(g.AsOf, horizon) <= 0)
                .Select(g => g.Demoted!).Distinct().Count();
            return (edgeIds.Count - wouldRetiredBy) / (double)edgeIds.Count;
        }
        return (At(5), At(10));
    }

    private string? SessionAsOf(int ordinal) =>
        db.Runs.Where(r => r.RunKind == Replay && r.Status == "ok")
            .OrderBy(r => r.AsOf).Select(r => r.AsOf).Skip(Math.Max(0, ordinal - 1)).FirstOrDefault();

    private Dictionary<string, int> FalseAlarmContributions(IReadOnlyCollection<string> noEdgeIds)
    {
        // Which signal put each alarmed no-edge plant into Suspect: parse the trigger_json contribution
        // tokens of its FIRST suspect status (per-signal contribution table, finding 114).
        var contributions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in SuspectEver(noEdgeIds))
        {
            var trigger = db.OverfittingStatus
                .Where(o => o.RunKind == Replay && o.StrategyId == id && (o.Status == "suspect" || o.Status == "retired"))
                .OrderBy(o => o.AsOf).Select(o => o.TriggerJson).FirstOrDefault();
            if (trigger is null) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(trigger);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var token = property.Value.GetString() ?? "";
                if (token.Contains("suspect", StringComparison.Ordinal) || token.Contains("critical", StringComparison.Ordinal))
                {
                    contributions[property.Name] = contributions.GetValueOrDefault(property.Name) + 1;
                }
            }
        }
        return contributions;
    }

    /// <summary>True when the plant's HELD-OUT (validate-segment) S3 percentile path stays on the far side of
    /// <paramref name="curve"/> for <c>curve.SustainEvals</c> CONSECUTIVE evals — below it when
    /// <paramref name="belowCurve"/> (the P_noise-breach / suspect signature), else above it (the P_edge-clear
    /// / distinguishable signature). Mirrors <see cref="MonitorSignals.S3Trajectory"/>'s sustain logic, applied
    /// OUT OF SAMPLE — the streak counts within validate only; track-days are the point's age since inception.</summary>
    private bool SustainsAcross(string id, string learnThrough, S3Curve curve, bool belowCurve)
    {
        var path = db.OverfittingChecks
            .Where(c => c.RunKind == Replay && c.Signal == "S3" && c.StrategyId == id && c.Value != null)
            .OrderBy(c => c.AsOf)
            .Select(c => new { c.AsOf, c.Value })
            .ToList();
        var consecutive = 0;
        for (var i = 0; i < path.Count; i++)
        {
            if (string.CompareOrdinal(path[i].AsOf, learnThrough) <= 0) continue;   // validate segment only
            var threshold = curve.At((i + 1) * gate.EvaluationCadenceDays);
            var onFarSide = belowCurve ? path[i].Value!.Value < threshold : path[i].Value!.Value >= threshold;
            if (onFarSide)
            {
                if (++consecutive >= curve.SustainEvals) return true;
            }
            else
            {
                consecutive = 0;
            }
        }
        return false;
    }

    private List<(double Pct, int TrackDays)> ValidatePercentilePoints(IReadOnlyCollection<string> ids, string learnThrough)
    {
        var points = new List<(double, int)>();
        foreach (var id in ids)
        {
            var path = db.OverfittingChecks
                .Where(c => c.RunKind == Replay && c.Signal == "S3" && c.StrategyId == id && c.Value != null)
                .OrderBy(c => c.AsOf)
                .Select(c => new { c.AsOf, c.Value })
                .ToList();
            for (var i = 0; i < path.Count; i++)
            {
                if (string.CompareOrdinal(path[i].AsOf, learnThrough) <= 0) continue;
                points.Add((path[i].Value!.Value, (i + 1) * gate.EvaluationCadenceDays));
            }
        }
        return points;
    }
}

/// <summary>
/// The paired D51-blend vs static equal-weight comparison over the replay generation (MASTER §1.2 /
/// §23.4): d_t = r_blend − r_equalweight over the same roster and cadence, NW-MDE'd like any other
/// pair. Persisted as ONE quarantined power_reports row (`allocator:d51-blend` vs
/// `allocator:equal-weight`, run_kind='replay') — the D58 read-model reads exactly that row.
/// </summary>
public static class AllocatorValueAddKpi
{
    public const string BlendId = "allocator:d51-blend";
    public const string EqualWeightId = "allocator:equal-weight";
    private const string Replay = "replay";

    public static AllocatorValueAdd? Compute(AlphaLabDbContext db, GateOptions gate, IReadOnlyList<PlantSpec> specs)
    {
        var allocations = db.AllocationLog
            .Where(a => a.RunKind == Replay)
            .OrderBy(a => a.AsOf)
            .Select(a => new { a.AsOf, a.WeightsJson })
            .ToList();
        if (allocations.Count == 0) return null;

        // Per-allocation weight vectors (strategy → weight), and the roster = every strategy ever weighted.
        var weightsByDate = new List<(string AsOf, Dictionary<string, double> Weights)>();
        foreach (var a in allocations)
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<Allocator.AllocationStep.WeightEntry>>(
                a.WeightsJson, AlphaLab.Core.Json.AlphaLabJson.Options) ?? [];
            weightsByDate.Add((a.AsOf, entries.ToDictionary(e => e.StrategyId, e => e.Weight, StringComparer.Ordinal)));
        }
        var roster = weightsByDate.SelectMany(w => w.Weights.Keys).Distinct().ToList();
        if (roster.Count == 0) return null;

        // Per-strategy daily returns from the replay equity curves.
        var returns = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var strategyId in roster)
        {
            var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == Replay);
            if (account is null) continue;
            var curve = db.EquityCurve.Where(e => e.AccountId == account.AccountId && e.RunKind == Replay)
                .OrderBy(e => e.AsOf).Select(e => new { e.AsOf, e.Equity }).ToList();
            var perDay = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 1; i < curve.Count; i++)
            {
                if (curve[i - 1].Equity > 0) perDay[curve[i].AsOf] = (double)(curve[i].Equity / curve[i - 1].Equity) - 1.0;
            }
            returns[strategyId] = perDay;
        }

        // d_t from the first allocation on: blend under the LAST weights ≤ t−1, equal weight over the
        // strategies those weights cover (the same roster at the same cadence — a paired comparison).
        var sessions = db.Runs.Where(r => r.RunKind == Replay && r.Status == "ok")
            .OrderBy(r => r.AsOf).Select(r => r.AsOf).ToList();
        var d = new List<double>();
        foreach (var day in sessions)
        {
            var active = weightsByDate.LastOrDefault(w => string.CompareOrdinal(w.AsOf, day) < 0);
            if (active.Weights is null || active.Weights.Count == 0) continue;
            double blend = 0, equal = 0;
            var covered = 0;
            foreach (var (strategyId, weight) in active.Weights)
            {
                if (!returns.TryGetValue(strategyId, out var perDay) || !perDay.TryGetValue(day, out var r)) continue;
                blend += weight * r;
                equal += r;
                covered++;
            }
            if (covered == 0) continue;
            d.Add(blend - equal / covered);
        }
        if (d.Count < 2) return null;

        var mde = MdeCalculator.Compute(d.ToArray(), 21, gate);
        var gap = d.Average() * MetricsConstants.TradingDaysPerYear;
        var verdict = Gate.PromotionGate.ToToken(Gate.PromotionGate.Decide(gap, mde.MdeAnn, d.Count, gate.MinTrackDays));

        // Behavioral halves of the KPI: mean applied weight on edge vs anti plants across evaluations.
        double MeanWeight(IReadOnlyCollection<string> ids) =>
            weightsByDate.Count == 0 || ids.Count == 0
                ? 0
                : weightsByDate.Average(w => ids.Sum(id => w.Weights.GetValueOrDefault(id)) / Math.Max(1, ids.Count)) * 100.0;
        var edgeIds = specs.Where(s => s.Kind == PlantKind.Edge).Select(s => s.StrategyId).ToHashSet(StringComparer.Ordinal);
        var antiIds = specs.Where(s => s.Kind == PlantKind.Anti).Select(s => s.StrategyId).ToHashSet(StringComparer.Ordinal);

        var result = new AllocatorValueAdd(gap, mde.MdeAnn, d.Count,
            verdict, MeanWeight(edgeIds), MeanWeight(antiIds));

        // Persist the single quarantined pair row (idempotent per as_of).
        var asOf = sessions.Count > 0 ? sessions[^1] : DateTime.MinValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!db.PowerReports.Any(p => p.RunKind == Replay && p.StrategyA == BlendId && p.AsOf == asOf))
        {
            db.PowerReports.Add(new Data.Entities.PowerReportRow
            {
                AsOf = asOf, StrategyA = BlendId, StrategyB = EqualWeightId,
                TDays = mde.TDays, SigmaLr = mde.SigmaLr, NwLag = mde.NwLag, MdeAnn = mde.MdeAnn,
                ObservedGapAnn = gap, Verdict = verdict, RunKind = Replay,
            });
            db.SaveChanges();
        }
        return result;
    }
}
