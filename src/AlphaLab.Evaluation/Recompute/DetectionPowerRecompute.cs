using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Metrics;

namespace AlphaLab.Evaluation.Recompute;

/// <summary>One monthly edge rung's detection curve, stored beside recomputed so the two are read as one
/// comparison rather than two tables.</summary>
public sealed record RungPower(
    double AlphaAnnPct, int Seeds,
    int StoredPromoted, int RecomputedPromoted,
    double StoredPAtHorizon, double RecomputedPAtHorizon,
    int? StoredMedianSessions, int? RecomputedMedianSessions);

/// <summary>The C-1 detection-power comparison and the floor each side implies.</summary>
public sealed record DetectionPowerComparison(
    int HorizonYears, double Power, int HorizonSessions,
    IReadOnlyList<RungPower> Rungs,
    double? StoredAlphaStarAnn, double? RecomputedAlphaStarAnn);

/// <summary>
/// Rebuilds the **C-1 detection-power curve** from RECOMPUTED promotions, beside the one generation 1's
/// promotions imply, and derives the empirical floor α*(H) from each (v1.9.73).
///
/// **Why this belongs in the harness.** §25.5(b) described the harness as something that would
/// "recompute the C-1 detection-power curve and the status-derived KPIs rather than re-simulate them" —
/// and the three §25.3 parity artefacts do not contain that curve. A rule change can move every promotion
/// in the generation (finding 285's alpha change moves 65 of 75) and the parity artefacts will report the
/// COUNT without answering the question the count is asked for: **does the detection floor move, and does
/// the gate reopen** (finding 336). Without this section the harness measures the input to that question
/// and stops short of it.
///
/// **It mirrors `CalibrationOrchestrator.DetectionPowerCurves` deliberately, including its denominator.**
/// The sweep is the MONTHLY edge ladder (Change 4/D101: daily cannot promote under its cost drag), the
/// grid is `Gate.EvaluationCadenceDays`, promotion is located by SESSION INDEX rather than by date, and
/// the denominator is the rung's full seeded cohort — a plant that never promoted still counts. Any of
/// those differing would make the recomputed curve incomparable with the frozen one, which is the only
/// thing it is for.
/// </summary>
public sealed class DetectionPowerRecompute(AlphaLabDbContext db, GateOptions gate, string runKind = "replay")
{
    private const string MonthlyEdgePrefix = "plant:edge:monthly:";

    public DetectionPowerComparison Build(IReadOnlyDictionary<string, string> recomputedPromotions)
    {
        ArgumentNullException.ThrowIfNull(recomputedPromotions);

        var sessions = db.Runs.Where(r => r.RunKind == runKind && r.Status == "ok")
            .OrderBy(r => r.AsOf).Select(r => r.AsOf).ToList();
        var horizonSessions = (int)(Math.Max(1, gate.DetectabilityHorizonYears) * MetricsConstants.TradingDaysPerYear);

        var stored = db.GoLiveLog
            .Where(g => g.RunKind == runKind && g.Verdict == "Promoted" && g.Promoted != null)
            .Select(g => new { Strategy = g.Promoted!, g.AsOf })
            .AsEnumerable()
            .GroupBy(g => g.Strategy, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(x => x.AsOf)!, StringComparer.Ordinal);

        // The seeded cohort per rung, from the strategies table — the honest denominator, and the same one
        // the frozen sweep used. (The pre-Change-4 residue of finding 341 is DAILY, so the monthly rungs
        // this reads are clean.)
        var cohorts = db.Strategies
            .Where(s => s.StrategyId.StartsWith(MonthlyEdgePrefix))
            .Select(s => s.StrategyId)
            .AsEnumerable()
            .GroupBy(RungOf, StringComparer.Ordinal)
            .Where(g => g.Key is not null)
            .ToDictionary(
                g => double.Parse(g.Key!, NumberStyles.Float, CultureInfo.InvariantCulture),
                g => g.ToList());

        var rungs = new List<RungPower>();
        var storedLevels = new List<(double AlphaPct, double PromotedAtH)>();
        var recomputedLevels = new List<(double AlphaPct, double PromotedAtH)>();

        foreach (var (alphaPct, ids) in cohorts.OrderBy(kv => kv.Key))
        {
            var storedIdx = SessionIndexes(ids, stored, sessions);
            var recomputedIdx = SessionIndexes(ids, recomputedPromotions, sessions);

            var storedP = Fraction(storedIdx, horizonSessions, ids.Count);
            var recomputedP = Fraction(recomputedIdx, horizonSessions, ids.Count);

            rungs.Add(new RungPower(
                alphaPct, ids.Count, storedIdx.Count, recomputedIdx.Count, storedP, recomputedP,
                Median(storedIdx), Median(recomputedIdx)));
            storedLevels.Add((alphaPct, storedP));
            recomputedLevels.Add((alphaPct, recomputedP));
        }

        return new DetectionPowerComparison(
            gate.DetectabilityHorizonYears, gate.Power, horizonSessions, rungs,
            storedLevels.Count == 0 ? null : DetectionCurves.AlphaStar(storedLevels, gate.Power),
            recomputedLevels.Count == 0 ? null : DetectionCurves.AlphaStar(recomputedLevels, gate.Power));
    }

    /// <summary>`plant:edge:monthly:{alpha}:{seed}` → the alpha token, or null if the id is not that shape.</summary>
    private static string? RungOf(string strategyId)
    {
        var rest = strategyId[MonthlyEdgePrefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? null : rest[..colon];
    }

    /// <summary>Promotion located by SESSION INDEX, matching the frozen sweep — a date grid would put the
    /// two curves on different x-axes.</summary>
    private static List<int> SessionIndexes(
        IEnumerable<string> ids, IReadOnlyDictionary<string, string> promotions, List<string> sessions)
    {
        var result = new List<int>();
        foreach (var id in ids)
        {
            if (!promotions.TryGetValue(id, out var asOf)) continue;
            var idx = sessions.FindIndex(s => string.CompareOrdinal(s, asOf) >= 0);
            if (idx >= 0) result.Add(idx + 1);
        }
        return result;
    }

    private static double Fraction(List<int> promotionSessions, int t, int cohortSize) =>
        cohortSize == 0 ? 0.0 : Math.Round(promotionSessions.Count(p => p <= t) / (double)cohortSize, 4);

    private static int? Median(List<int> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
