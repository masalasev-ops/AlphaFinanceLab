using AlphaLab.Data;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Evaluation.Monitor;

/// <summary>
/// Computes + persists the turnover-match check for every promotable strategy each evaluation (finding
/// 115). The strategy's realized annualized turnover comes from its trades over the window; the matched
/// population's turnover distribution is RE-SIMULATED (a member sample) via the deterministic
/// <see cref="PopulationEngine"/> — turnover depends only on the selections, not on returns, so this needs
/// no equity. Writes status-neutral turnover_match rows (never aggregated into overfitting_status).
/// </summary>
public sealed class TurnoverMatchStep(AlphaLabDbContext db, double tolerancePct)
{
    private const int PopulationSampleSize = 20;   // enough for a stable median + band, far cheaper than all M
    private const string RunKindLive = "live";

    public void Run(string asOf, IReadOnlyList<string> windowDates, PopulationEngine engine, PopulationFamily matchedFamily,
        string benchmarkStrategyId, string runKind = RunKindLive)
    {
        if (windowDates.Count < 2) return;

        // Population turnover distribution (a member sample; each member's mean daily one-way turnover, annualized).
        var sample = Math.Min(PopulationSampleSize, matchedFamily.Size);
        var populationTurnovers = new List<double>(sample);
        for (var i = 0; i < sample; i++)
        {
            var days = engine.SimulateMember(matchedFamily, i, 100_000m, windowDates);
            var meanOneWay = days.Skip(1).Average(d => d.TurnoverOneWay);   // skip the inception (initial buy) day
            populationTurnovers.Add(meanOneWay * MetricsConstants.TradingDaysPerYear);
        }

        // Run-kind-scoped status (Phase 4/D37) — a replay-retired strategy drops its caveat rows too.
        var promotable = EffectiveStatus.Resolve(db, runKind)
            .Where(kv => kv.Value is "candidate" or "live")
            .Select(kv => kv.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        foreach (var strategyId in promotable)
        {
            if (strategyId == benchmarkStrategyId) continue;
            var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == runKind);
            if (account is null) continue;

            var strategyTurnover = StrategyTurnover(account.AccountId, windowDates, runKind);
            TurnoverMatch.WriteCheck(db, asOf, strategyId, strategyTurnover, populationTurnovers, tolerancePct, runKind);
        }
    }

    private double StrategyTurnover(long accountId, IReadOnlyList<string> windowDates, string runKind)
    {
        var from = windowDates[0];
        var to = windowDates[^1];

        var buyNotional = db.Trades
            .Where(t => t.AccountId == accountId && t.RunKind == runKind && t.Side == "buy"
                        && string.Compare(t.FilledOn, from) >= 0 && string.Compare(t.FilledOn, to) <= 0)
            .Select(t => new { t.RawFillPrice, t.Shares })
            .AsEnumerable()
            .Sum(t => (double)t.RawFillPrice * t.Shares);

        var equities = db.EquityCurve
            .Where(e => e.AccountId == accountId && e.RunKind == runKind
                        && string.Compare(e.AsOf, from) >= 0 && string.Compare(e.AsOf, to) <= 0)
            .Select(e => e.Equity)
            .ToList();
        var avgEquity = equities.Count > 0 ? (double)equities.Average() : 0.0;

        return StrategyMetrics.AnnualizedTurnover(buyNotional, avgEquity, windowDates.Count);
    }
}
