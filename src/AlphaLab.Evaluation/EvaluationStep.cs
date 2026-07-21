using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
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
        var benchCurve = CurveMath.Curve(db, benchAccount.AccountId, runKind);
        if (benchCurve.Count < 2) return [];

        // The NW lag is driven by the LARGER of the two horizons (MdeCalculator contract: L = min(2·max(hA,hB),
        // cap)). The benchmark is the cap-weight Buy&Hold, whose null horizon maps to the conservative default,
        // so a short-horizon strategy still gets the full-lag autocorrelation correction — never an under-set
        // lag that under-claims the MDE and lets a gap inside the true MDE read Promoted (hard rule 6).
        var benchHorizon = db.Strategies
            .Where(s => s.StrategyId == benchmarkStrategyId)
            .Select(s => s.HoldingHorizonDays)
            .FirstOrDefault() ?? DefaultHorizonDays;

        var promotable = db.Strategies
            .Where(s => s.Status == "candidate" || s.Status == "live")
            .Select(s => new { s.StrategyId, s.HoldingHorizonDays, s.Status })
            .ToList();

        var results = new List<PairEvaluation>();
        foreach (var strat in promotable)
        {
            if (strat.StrategyId == benchmarkStrategyId) continue;

            var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strat.StrategyId && a.RunKind == runKind);
            if (account is null) continue;

            var stratCurve = CurveMath.Curve(db, account.AccountId, runKind);
            if (stratCurve.Count < 2) continue;

            var (stratReturns, benchReturns) = CurveMath.AlignedReturns(stratCurve, benchCurve);
            if (stratReturns.Count < 2) continue;

            var d = new double[stratReturns.Count];
            for (var i = 0; i < d.Length; i++) d[i] = stratReturns[i] - benchReturns[i];

            var maxHorizon = Math.Max(strat.HoldingHorizonDays ?? DefaultHorizonDays, benchHorizon);
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

            // Promotion (D31): a candidate that earns Promoted goes live and the event is logged. The gate
            // only ever PROMOTES here — a Refused verdict is not a kill (D63 reserves fast-kills for the
            // anti-predictive S3/S6 breaches + the trade track); demotion/retire is the monitor's (3.6).
            if (verdict == PromotionVerdict.Promoted && strat.Status == "candidate")
            {
                db.Strategies.First(s => s.StrategyId == strat.StrategyId).Status = "live";
                db.GoLiveLog.Add(new GoLiveLogRow
                {
                    AsOf = asOf,
                    Promoted = strat.StrategyId,
                    Verdict = PromotionGate.ToToken(verdict),
                    EvidenceJson = JsonSerializer.Serialize(
                        new { strategy = strat.StrategyId, benchmark = benchmarkStrategyId, observed_gap_ann = gap, mde_ann = mde.MdeAnn, t_days = mde.TDays, sigma_lr = mde.SigmaLr },
                        AlphaLabJson.Options),
                    RunKind = runKind,
                });
            }

            results.Add(new PairEvaluation(
                strat.StrategyId, benchmarkStrategyId, mde.TDays, mde.SigmaLr, mde.NwLag, mde.MdeAnn, gap, verdict));
        }

        db.SaveChanges();
        return results;
    }

}
