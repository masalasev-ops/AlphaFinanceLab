using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Candidates;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Ai;

/// <summary>The as-of floor and the trials count it was computed at. Both, because the floor RISES with
/// the trials tax and is uninterpretable without the count that set it. <c>CeilingAnn</c> is D116's
/// plausibility ceiling at the same as-of — the pack carries BOTH ends, because a pack showing only the
/// floor gives the seat one scale cue and it points up (finding 337).</summary>
public sealed record AsOfFloor(
    double? FloorAnn, int TrialsCount, string? Reason, double? CeilingAnn = null, string? CeilingState = null);

/// <summary>
/// The arena's detectability floor, resolved **as-of** — for the D104 context-pack field.
///
/// **Why this is not <c>DetectabilityGate.Assess</c>.** That method resolves CURRENT state by design:
/// <c>ResolveCurrent(DetectionPower)</c>, a live trials count, and the most recent 50 <c>power_reports</c>
/// — because, as its own comment says, "admission is an operational act, not a run-scoped read". That is
/// correct for admission and **wrong for a pack**: wiring the operational path into a context pack would
/// put a post-as-of fact in it, and `FX-PackNoLeak` would (correctly) redden.
///
/// So the same arithmetic is computed over as-of-bounded inputs: trials registered on or before the
/// as-of, σ from power reports on or before it, and the detection-power row through D96's
/// <c>ResolveAsOf</c>. **Two read paths for one quantity, deliberately** — the operational one and the
/// point-in-time one — because they answer different questions and collapsing them would silently make
/// one of them wrong.
/// </summary>
public sealed class AsOfDetectabilityFloor(AlphaLabDbContext db, GateOptions gate)
{
    public AsOfFloor Resolve(string asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var horizonSessions = (int)(Math.Max(1, gate.DetectabilityHorizonYears) * MetricsConstants.TradingDaysPerYear);

        // Trials AS OF: a candidate registered after the as-of has not yet spent anyone's significance.
        var trialsAfter = db.TrialsRegistry
            .Count(t => t.RunKind == "live" && string.Compare(t.RegisteredOn, asOf) <= 0) + 1;

        // α*(H) and the D116 ceiling from ONE as-of read of the detection-power row (D96 ResolveAsOf, so a
        // pack cannot see a threshold version that did not exist on its day). Shared arithmetic with the
        // gate deliberately: the two READ paths differ, what they compute from the row must not.
        var bounds = DetectionCurves.Resolve(
            new ConfigReadService(db).ResolveAsOf(CalibratedKeys.DetectionPower, asOf), horizonSessions, gate.Power);

        var sigma = ResolveSigmaAsOf(asOf);
        if (sigma is null)
        {
            // No σ estimable at that as-of ⇒ no honest floor. Reported as a REASON rather than a zero:
            // a zero floor would say "anything is detectable", which is the opposite of what is known.
            return new AsOfFloor(null, trialsAfter, "unassessed_no_sigma", bounds.CeilingAnn, bounds.CeilingState);
        }

        var analytic = BonferroniZSum(trialsAfter) * sigma.Value
                       * MetricsConstants.TradingDaysPerYear / Math.Sqrt(horizonSessions);
        var floor = Math.Max(analytic, bounds.AlphaStarAnn ?? 0.0);

        // D116's third valve, same rule as the gate: a ceiling at or below the floor is reported, not applied.
        var ceilingState = bounds.CeilingState == DetectionCurves.CeilingApplied
                           && bounds.CeilingAnn is { } c && c <= floor
            ? DetectionCurves.CeilingInert
            : bounds.CeilingState;

        return new AsOfFloor(floor, trialsAfter, null, bounds.CeilingAnn, ceilingState);
    }

    private double BonferroniZSum(int trialsAfter) =>
        Normal.InvCdf(1.0 - (1.0 - gate.Confidence) / (2.0 * Math.Max(1, trialsAfter)))
        + Normal.InvCdf(gate.Power);

    private double? ResolveSigmaAsOf(string asOf)
    {
        double? Median(string runKind)
        {
            var sigmas = db.PowerReports
                .Where(p => p.RunKind == runKind && p.SigmaLr > 0 && string.Compare(p.AsOf, asOf) <= 0)
                .OrderByDescending(p => p.AsOf)
                .Select(p => p.SigmaLr)
                .Take(50)
                .ToList();
            if (sigmas.Count == 0) return null;
            sigmas.Sort();
            return sigmas[sigmas.Count / 2];
        }
        return Median("live") ?? Median("replay");
    }

}
