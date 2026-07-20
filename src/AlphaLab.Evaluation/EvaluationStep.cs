using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Gate;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Power;

namespace AlphaLab.Evaluation;

/// <summary>One promotable strategy's paired evaluation against the benchmark on one day.</summary>
public readonly record struct PairEvaluation(
    string StrategyId, string BenchmarkId, int TDays, double SigmaLr, int NwLag,
    double MdeAnn, double ObservedGapAnn, PromotionVerdict Verdict);

/// <summary>
/// The 21-day evaluation step (D31/D48). Runs AFTER the daily Stage-2 write commits, in its own
/// transaction (keeps the &lt;60s daily budget clean; the cadence work is amortized). For each promotable
/// strategy it forms the pair against the cap-weight benchmark (D26), computes the daily active-return
/// difference d_t, the NW-corrected MDE, and the observed annualized gap, then persists a power_reports
/// row with the gate verdict. The go_live_log promotion EVENT + the status transition layer on top
/// (checkpoint 3.5); the monitor + allocator run in the same step (3.6/3.7).
///
/// A reader of the store: it reads equity_curve (run_kind='live') and writes power_reports via the
/// caller's transaction (the Worker owns the commit; D59 sole writer).
/// </summary>
public sealed class EvaluationStep(AlphaLabDbContext db, GateOptions gate)
{
    /// <summary>The Jensen's-alpha benchmark (D26): the cap-weight Buy&amp;Hold account. A stable frozen id
    /// (STRATEGY_CATALOG §5.1); parameterizable so a synthetic arena can designate its own benchmark.</summary>
    public const string DefaultBenchmarkStrategyId = "buyhold:cw";

    /// <summary>Horizon for a strategy with no day-count shape (Buy&amp;Hold / to-rank-exit): the
    /// conservative default drives the NW lag to the cap, maximizing the autocorrelation correction so the
    /// MDE never under-claims.</summary>
    private const int DefaultHorizonDays = 21;

    private const string RunKindLive = "live";

    public IReadOnlyList<PairEvaluation> Run(string asOf, string benchmarkStrategyId = DefaultBenchmarkStrategyId, string runKind = RunKindLive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var benchAccount = db.Accounts.FirstOrDefault(a => a.StrategyId == benchmarkStrategyId && a.RunKind == runKind);
        if (benchAccount is null) return [];
        var benchCurve = Curve(benchAccount.AccountId, runKind);
        if (benchCurve.Count < 2) return [];

        var promotable = db.Strategies
            .Where(s => s.Status == "candidate" || s.Status == "live")
            .Select(s => new { s.StrategyId, s.HoldingHorizonDays })
            .ToList();

        var results = new List<PairEvaluation>();
        foreach (var strat in promotable)
        {
            if (strat.StrategyId == benchmarkStrategyId) continue;

            var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strat.StrategyId && a.RunKind == runKind);
            if (account is null) continue;

            var stratCurve = Curve(account.AccountId, runKind);
            if (stratCurve.Count < 2) continue;

            var (stratReturns, benchReturns) = AlignedReturns(stratCurve, benchCurve);
            if (stratReturns.Count < 2) continue;

            var d = new double[stratReturns.Count];
            for (var i = 0; i < d.Length; i++) d[i] = stratReturns[i] - benchReturns[i];

            var maxHorizon = strat.HoldingHorizonDays ?? DefaultHorizonDays;
            var mde = MdeCalculator.Compute(d, maxHorizon, gate);
            var gap = d.Average() * MetricsConstants.TradingDaysPerYear;
            var verdict = PromotionGate.Decide(gap, mde.MdeAnn, d.Length, gate.MinTrackDays);

            db.PowerReports.Add(new PowerReportRow
            {
                AsOf = asOf,
                StrategyA = strat.StrategyId,
                StrategyB = benchmarkStrategyId,
                TDays = mde.TDays,
                SigmaLr = mde.SigmaLr,
                NwLag = mde.NwLag,
                MdeAnn = mde.MdeAnn,
                ObservedGapAnn = gap,
                Verdict = PromotionGate.ToToken(verdict),
                RunKind = runKind,
            });

            results.Add(new PairEvaluation(
                strat.StrategyId, benchmarkStrategyId, mde.TDays, mde.SigmaLr, mde.NwLag, mde.MdeAnn, gap, verdict));
        }

        db.SaveChanges();
        return results;
    }

    private List<(string AsOf, decimal Equity)> Curve(long accountId, string runKind) =>
        db.EquityCurve
            .Where(e => e.AccountId == accountId && e.RunKind == runKind)
            .OrderBy(e => e.AsOf)
            .Select(e => new { e.AsOf, e.Equity })
            .AsEnumerable()
            .Select(e => (e.AsOf, e.Equity))
            .ToList();

    // Returns between consecutive dates common to both curves (both are daily forward curves, so the
    // common set is their overlapping range). rf cancels in the difference, so it is omitted here.
    private static (List<double> Strat, List<double> Bench) AlignedReturns(
        List<(string AsOf, decimal Equity)> strat, List<(string AsOf, decimal Equity)> bench)
    {
        var benchByDate = new Dictionary<string, decimal>(bench.Count);
        foreach (var (asOf, equity) in bench) benchByDate[asOf] = equity;

        var common = strat.Where(s => benchByDate.ContainsKey(s.AsOf)).ToList();   // strat already ordered by as_of
        var stratRet = new List<double>(Math.Max(0, common.Count - 1));
        var benchRet = new List<double>(Math.Max(0, common.Count - 1));

        for (var i = 1; i < common.Count; i++)
        {
            var sPrev = common[i - 1].Equity;
            var sNow = common[i].Equity;
            var bPrev = benchByDate[common[i - 1].AsOf];
            var bNow = benchByDate[common[i].AsOf];
            if (sPrev <= 0m || bPrev <= 0m) continue;
            stratRet.Add((double)(sNow / sPrev) - 1.0);
            benchRet.Add((double)(bNow / bPrev) - 1.0);
        }

        return (stratRet, benchRet);
    }
}
