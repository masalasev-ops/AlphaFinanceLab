using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Candidates;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Ai;

/// <summary>The as-of floor and the trials count it was computed at. Both, because the floor RISES with
/// the trials tax and is uninterpretable without the count that set it.</summary>
public sealed record AsOfFloor(double? FloorAnn, int TrialsCount, string? Reason);

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

        var sigma = ResolveSigmaAsOf(asOf);
        if (sigma is null)
        {
            // No σ estimable at that as-of ⇒ no honest floor. Reported as a REASON rather than a zero:
            // a zero floor would say "anything is detectable", which is the opposite of what is known.
            return new AsOfFloor(null, trialsAfter, "unassessed_no_sigma");
        }

        var analytic = BonferroniZSum(trialsAfter) * sigma.Value
                       * MetricsConstants.TradingDaysPerYear / Math.Sqrt(horizonSessions);
        var empirical = EmpiricalAlphaStarAsOf(asOf, horizonSessions);
        var floor = Math.Max(analytic, empirical ?? 0.0);

        return new AsOfFloor(floor, trialsAfter, null);
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

    /// <summary>α*(H) from the frozen detection-power row, resolved through <c>ResolveAsOf</c> (D96) so a
    /// pack cannot see a threshold version that did not exist on its day.</summary>
    private double? EmpiricalAlphaStarAsOf(string asOf, int horizonSessions)
    {
        var json = new ConfigReadService(db).ResolveAsOf(CalibratedKeys.DetectionPower, asOf);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("curves", out var curves)) return null;

        var levels = new List<(double AlphaPct, double PromotedAtH)>();
        foreach (var property in curves.EnumerateObject())
        {
            if (!double.TryParse(property.Name, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var alphaPct)) continue;
            levels.Add((alphaPct, InterpolatePromoted(property.Value, horizonSessions)));
        }
        if (levels.Count == 0) return null;
        levels.Sort((a, b) => a.AlphaPct.CompareTo(b.AlphaPct));

        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i].PromotedAtH < gate.Power) continue;
            if (i == 0) return levels[0].AlphaPct / 100.0;
            var (lo, hi) = (levels[i - 1], levels[i]);
            var w = (gate.Power - lo.PromotedAtH) / (hi.PromotedAtH - lo.PromotedAtH);
            return (lo.AlphaPct + w * (hi.AlphaPct - lo.AlphaPct)) / 100.0;
        }
        return double.PositiveInfinity;
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
        return prev?.P ?? 0;
    }
}
