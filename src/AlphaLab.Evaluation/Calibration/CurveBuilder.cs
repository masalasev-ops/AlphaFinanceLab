using AlphaLab.Data;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Calibration;

/// <summary>
/// Builds the D56 trajectory curves from the replay generation's persisted S3 percentile paths
/// (overfitting_checks, signal='S3', run_kind='replay' — the D88 covering index serves the read).
///
/// P_edge(t): the per-evaluation-index MEDIAN of the edge seeds' percentiles (band = their 25–75%).
/// P_noise(t): the per-index FALSE-ALARM-RATE quantile of the no-edge seeds' percentile distribution —
/// the envelope below which a genuinely edgeless strategy falls only at the configured rate (D56).
///
/// FR-42: paths are truncated at the learn/validate boundary BEFORE any statistic is computed — no
/// validate-period datum reaches a learn-period computation (FX-ReplayPartition-NoLeak).
/// </summary>
public static class CurveBuilder
{
    /// <summary>Per-strategy S3 percentile paths on the evaluation grid, ordered by as_of; rows after
    /// <paramref name="learnThrough"/> (when set) are EXCLUDED — the FR-42 partition.</summary>
    public static Dictionary<string, List<double>> PercentilePaths(
        AlphaLabDbContext db, IReadOnlyCollection<string> strategyIds, string? learnThrough)
    {
        var rows = db.OverfittingChecks
            .Where(c => c.RunKind == "replay" && c.Signal == "S3" && c.Value != null && strategyIds.Contains(c.StrategyId))
            .OrderBy(c => c.StrategyId).ThenBy(c => c.AsOf)
            .Select(c => new { c.StrategyId, c.AsOf, c.Value })
            .AsEnumerable()
            .Where(c => learnThrough is null || string.CompareOrdinal(c.AsOf, learnThrough) <= 0);

        var paths = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            (paths.TryGetValue(row.StrategyId, out var list) ? list : paths[row.StrategyId] = []).Add(row.Value!.Value);
        }
        return paths;
    }

    /// <summary>P_edge(t): per-index median over seeds + the archived 25–75 band.</summary>
    public static S3Curve BuildEdge(
        IReadOnlyCollection<List<double>> edgePaths, string family, int evalCadenceDays,
        int sustainEvals, double falseAlarmRate, int populationM, CurveVintage? vintage) =>
        Build("p_edge", edgePaths, family, evalCadenceDays, sustainEvals, falseAlarmRate, populationM,
            xs => Statistics.Percentile(xs, 50), anchorQuantile: 0.95, vintage);

    /// <summary>P_noise(t): per-index false-alarm-rate quantile of the no-edge seeds' distribution.</summary>
    public static S3Curve BuildNoise(
        IReadOnlyCollection<List<double>> noEdgePaths, string family, int evalCadenceDays,
        int sustainEvals, double falseAlarmRate, int populationM, CurveVintage? vintage) =>
        Build("p_noise", noEdgePaths, family, evalCadenceDays, sustainEvals, falseAlarmRate, populationM,
            xs => Statistics.Percentile(xs, falseAlarmRate * 100.0), anchorQuantile: falseAlarmRate, vintage);

    private static S3Curve Build(
        string kind, IReadOnlyCollection<List<double>> paths, string family, int evalCadenceDays,
        int sustainEvals, double falseAlarmRate, int populationM,
        Func<List<double>, double> reduce, double anchorQuantile, CurveVintage? vintage)
    {
        if (paths.Count == 0 || paths.All(p => p.Count == 0))
        {
            throw new InvalidOperationException($"no S3 percentile paths to build the {kind} curve from (fail closed).");
        }

        var maxLen = paths.Max(p => p.Count);
        var knots = new List<CurveKnot>(maxLen);
        var band = new List<BandKnot>(maxLen);
        for (var i = 0; i < maxLen; i++)
        {
            var at = paths.Where(p => p.Count > i).Select(p => p[i]).ToList();
            if (at.Count == 0) continue;
            var t = (i + 1) * evalCadenceDays;   // evaluation index → nominal track length
            knots.Add(new CurveKnot(t, Math.Round(reduce(at), 4)));
            band.Add(new BandKnot(t, Math.Round(Statistics.Percentile(at, 25), 4), Math.Round(Statistics.Percentile(at, 75), 4)));
        }

        // C-2: the anchor rides an M-member empirical distribution — sqrt(M·q·(1−q)) members of
        // binomial noise at the defining quantile (≈ ±3.1 at M=200/q=0.95; the "~±1.5 members" figure
        // in the register is the one-sided half of the ±1σ band — archived, either way, as evidence).
        var samplingBand = Math.Sqrt(populationM * anchorQuantile * (1 - anchorQuantile));

        return new S3Curve(kind, family, "piecewise_linear", sustainEvals, falseAlarmRate,
            knots, band, Math.Round(samplingBand, 2), vintage);
    }
}
