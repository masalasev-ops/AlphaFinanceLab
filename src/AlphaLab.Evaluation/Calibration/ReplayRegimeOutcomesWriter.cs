using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Metrics;

namespace AlphaLab.Evaluation.Calibration;

/// <summary>
/// FR-41 (D89, forward-provision): replay strategy outcomes decomposed by REPLAY regime episode (the
/// D93 chain), persisted to `replay_regime_outcomes` under the D37 quarantine. Rows aggregate to the
/// overall replay outcome (Σ n_days = the strategy's labeled replay days — FX-ReplayPerRegime asserts
/// the identity) and never reach a forward view (no forward reader exists; the quarantine test pins
/// it). Not load-bearing for the Phase-4 DoD — recorded for the post-Phase-8 multi-regime survival
/// requirement. Idempotent per (strategy, episode): a re-run overwrites its own summary.
/// </summary>
public sealed class ReplayRegimeOutcomesWriter(AlphaLabDbContext db)
{
    private const string Replay = "replay";
    private const int DefaultLag = 21;

    /// <summary>Decompose every replay strategy's track by episode. <paramref name="benchmarkStrategyId"/>
    /// anchors the per-episode Jensen's alpha (the same benchmark the gate pairs against).</summary>
    public int Write(string benchmarkStrategyId)
    {
        var episodes = db.RegimeEpisodes.Where(e => e.RunKind == Replay).OrderBy(e => e.StartDate).ToList();
        if (episodes.Count == 0) return 0;

        var benchAccount = db.Accounts.FirstOrDefault(a => a.StrategyId == benchmarkStrategyId && a.RunKind == Replay);
        if (benchAccount is null) return 0;
        var benchCurve = Curve(benchAccount.AccountId);

        // The D41 risk-free series, once per write (checkpoint 6.6). Replay-only table under the D37
        // quarantine, but the arithmetic must match the monitor's or the regime decomposition and the
        // signal it decomposes would be computed on different definitions of alpha.
        var riskFree = RiskFreeSeries.Load(db);

        var written = 0;
        var accounts = db.Accounts.Where(a => a.RunKind == Replay).ToList();
        foreach (var account in accounts)
        {
            if (account.StrategyId == benchmarkStrategyId) continue;
            var curve = Curve(account.AccountId);
            if (curve.Count < 2) continue;

            foreach (var episode in episodes)
            {
                // The episode's day span: [start_date, end_date] (an open episode runs to the window end).
                var span = curve.Where(c =>
                        string.CompareOrdinal(c.AsOf, episode.StartDate) >= 0
                        && (episode.EndDate is null || string.CompareOrdinal(c.AsOf, episode.EndDate) <= 0))
                    .ToList();
                if (span.Count < 2) continue;

                var benchSpan = benchCurve.Where(c =>
                        string.CompareOrdinal(c.AsOf, episode.StartDate) >= 0
                        && (episode.EndDate is null || string.CompareOrdinal(c.AsOf, episode.EndDate) <= 0))
                    .ToList();
                var (sr, br) = AlignedExcessReturns(span, benchSpan, riskFree);
                if (sr.Count < 2) continue;

                var edgeAnn = SafeAlphaAnn(sr, br);
                var percentiles = db.OverfittingChecks
                    .Where(c => c.RunKind == Replay && c.Signal == "S3" && c.StrategyId == account.StrategyId
                                && c.Value != null
                                && string.Compare(c.AsOf, episode.StartDate) >= 0
                                && (episode.EndDate == null || string.Compare(c.AsOf, episode.EndDate) <= 0))
                    .Select(c => c.Value!.Value)
                    .ToList();
                double? medianPct = percentiles.Count > 0 ? Median(percentiles) : null;

                // n_days = SESSION count contributing in this episode (SCHEMA) — episodes partition the
                // window's dates, so Σ n_days over a strategy's rows equals its labeled replay days
                // (the FX-ReplayPerRegime aggregation identity).
                var row = db.ReplayRegimeOutcomes.Find(account.StrategyId, episode.EpisodeId, Replay);
                if (row is null)
                {
                    db.ReplayRegimeOutcomes.Add(new ReplayRegimeOutcomeRow
                    {
                        StrategyId = account.StrategyId,
                        RegimeEpisodeId = episode.EpisodeId,
                        RunKind = Replay,
                        EdgeAnn = edgeAnn,
                        MedianPercentile = medianPct,
                        NDays = span.Count,
                    });
                }
                else
                {
                    row.EdgeAnn = edgeAnn;
                    row.MedianPercentile = medianPct;
                    row.NDays = span.Count;
                }
                written++;
            }
        }
        db.SaveChanges();
        return written;
    }

    private List<(string AsOf, decimal Equity)> Curve(long accountId) =>
        db.EquityCurve.Where(e => e.AccountId == accountId && e.RunKind == Replay)
            .OrderBy(e => e.AsOf)
            .Select(e => new { e.AsOf, e.Equity })
            .AsEnumerable()
            .Select(e => (e.AsOf, e.Equity))
            .ToList();

    /// <summary>
    /// Aligned EXCESS returns for an episode span (D41, checkpoint 6.6).
    ///
    /// This was a THIRD hand-copy of the common-date-intersection + `prev ≤ 0` skip rule — the same
    /// duplication `BandInputs` carried under a comment warning that an off-by-one "would silently window
    /// the wrong days". It now delegates to `CurveMath.AlignedReturnsDated`, so the rule lives in one
    /// place, and the dates it returns are what make the per-day risk-free subtraction possible at all.
    /// </summary>
    private static (List<double> Strat, List<double> Bench) AlignedExcessReturns(
        IReadOnlyList<(string AsOf, decimal Equity)> strat, IReadOnlyList<(string AsOf, decimal Equity)> bench,
        RiskFreeSeries riskFree)
    {
        var (sr, br, dates) = CurveMath.AlignedReturnsDated(strat, bench);
        var rf = riskFree.For(dates);
        return (RiskFreeSeries.Excess(sr, rf), RiskFreeSeries.Excess(br, rf));
    }

    // Per-episode Jensen's alpha, with the monitor's degenerate-safe fallback (a constant-benchmark
    // episode falls back to the mean active return).
    private static double SafeAlphaAnn(IReadOnlyList<double> strat, IReadOnlyList<double> bench)
    {
        try
        {
            return StrategyMetrics.JensenAlpha(strat, bench, 0.0, DefaultLag).AlphaAnnualized;
        }
        catch (ArgumentException)
        {
            double m = 0;
            for (var i = 0; i < strat.Count; i++) m += strat[i] - bench[i];
            return m / strat.Count * MetricsConstants.TradingDaysPerYear;
        }
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
    }
}
