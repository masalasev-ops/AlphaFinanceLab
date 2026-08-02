using System.Globalization;
using System.Text.Json;

namespace AlphaLab.Evaluation.Calibration;

/// <summary>
/// The pure curve arithmetic over a frozen <c>Calibration.DetectionPower</c> row: the empirical floor
/// α*(H) and — from D116 — the plausibility CEILING, both read off the same swept plant ladder.
///
/// **Why this is shared and the read paths are not.** `DetectabilityGate` resolves the row through
/// `ResolveCurrent` (admission is an operational act) and `AsOfDetectabilityFloor` through `ResolveAsOf`
/// (a pack may not see a threshold version that post-dates its day). That separation is deliberate and
/// stays. What must NOT differ is what the two compute once they hold the row: D116's ceiling is derived
/// from the same levels that yield the floor, so a second copy of THIS arithmetic is exactly how the two
/// ends of the admissible band would silently stop agreeing with each other.
///
/// Levels are read from the <c>curves</c> object's KEYS, not from the row's <c>alphas_ann_pct</c> array:
/// the keys are what α* has always been computed from, and a ceiling derived from a different field could
/// disagree with its own floor if a future writer ever let the two drift.
/// </summary>
public static class DetectionCurves
{
    /// <summary>The ceiling was derived and is comparable against the floor.</summary>
    public const string CeilingApplied = "applied";

    /// <summary>No frozen row, no <c>curves</c> object, or no parseable level: nothing to derive from.</summary>
    public const string CeilingNoCurves = "unavailable_no_curves";

    /// <summary>Fewer than two distinct positive rungs: the ladder has no STEP, and D116's ceiling is the
    /// top rung times that step. Refusing to invent one is the point (finding 309).</summary>
    public const string CeilingNoStep = "unavailable_no_step";

    /// <summary>Derived, but at or below the floor — it cannot bind without producing an empty admissible
    /// band, so it is reported and NOT applied (D116's third valve).</summary>
    public const string CeilingInert = "inert_below_floor";

    /// <param name="AlphaStarAnn">The empirical floor: the smallest rung reaching <c>power</c> by the
    /// horizon (interpolated), <c>+∞</c> when no rung does, or null with no curves.</param>
    /// <param name="CeilingAnn">D116's ceiling as an annualized FRACTION, or null when underivable.</param>
    /// <param name="TopRungAnn">The largest swept rung — carried so a refusal message can say what the
    /// best-case simulated edge actually reached rather than printing a formatted infinity.</param>
    public sealed record Bounds(
        double? AlphaStarAnn,
        double? CeilingAnn,
        string CeilingState,
        double? TopRungAnn,
        double? TopRungPromotedAtH);

    private static readonly Bounds NoCurves = new(null, null, CeilingNoCurves, null, null);

    public static Bounds Resolve(string? detectionPowerJson, int horizonSessions, double power)
    {
        if (detectionPowerJson is null) return NoCurves;

        using var doc = JsonDocument.Parse(detectionPowerJson);
        if (!doc.RootElement.TryGetProperty("curves", out var curves)) return NoCurves;

        var levels = new List<(double AlphaPct, double PromotedAtH)>();
        foreach (var property in curves.EnumerateObject())
        {
            if (!double.TryParse(property.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out var alphaPct))
                continue;
            levels.Add((alphaPct, InterpolatePromoted(property.Value, horizonSessions)));
        }
        if (levels.Count == 0) return NoCurves;
        levels.Sort((a, b) => a.AlphaPct.CompareTo(b.AlphaPct));

        var (ceiling, ceilingState) = Ceiling(levels);
        var top = levels[^1];
        return new Bounds(AlphaStar(levels, power), ceiling, ceilingState, top.AlphaPct / 100.0, top.PromotedAtH);
    }

    /// <summary>D116: the top rung times the ladder's OWN geometric step, taken from the top two rungs.
    /// One step of headroom rather than none, because the floor is selected from this same ladder and
    /// <c>ceiling = max(rung)</c> collapses the admissible band to a point whenever the only rung reaching
    /// power IS the top rung — which is this arena's rule-selected primary.</summary>
    private static (double? Ceiling, string State) Ceiling(List<(double AlphaPct, double PromotedAtH)> levels)
    {
        var distinct = levels.Select(l => l.AlphaPct).Where(a => a > 0).Distinct().ToList();
        if (distinct.Count < 2) return (null, CeilingNoStep);

        var top = distinct[^1];
        var step = top / distinct[^2];
        return (top * step / 100.0, CeilingApplied);
    }

    private static double? AlphaStar(List<(double AlphaPct, double PromotedAtH)> levels, double power)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i].PromotedAtH < power) continue;
            if (i == 0) return levels[0].AlphaPct / 100.0;   // the lowest swept level already clears
            var (lo, hi) = (levels[i - 1], levels[i]);
            var w = (power - lo.PromotedAtH) / (hi.PromotedAtH - lo.PromotedAtH);
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
