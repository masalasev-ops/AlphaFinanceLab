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

        // Status is run-kind-scoped (Phase 4/D37): forward reads strategies.status verbatim; replay
        // derives its own promote/retire history from the quarantined records and NEVER consults the
        // forward column as its own state.
        var effective = EffectiveStatus.Resolve(db, runKind);
        var promotable = db.Strategies
            .Select(s => new { s.StrategyId, s.HoldingHorizonDays })
            .AsEnumerable()
            .Where(s => effective.GetValueOrDefault(s.StrategyId) is "candidate" or "live")
            .ToList();

        // THIS EVALUATION'S MONITOR STATUS, read the same way AllocationStep reads it (D156).
        // OVERFITTING_MONITOR §3: "Suspect ⇒ promotion vetoed regardless of P&L". Until D156 the gate ran
        // BEFORE the monitor, so it could only ever have seen the PREVIOUS evaluation's status — and the
        // veto was therefore unimplementable in the place the rule names. DailyPipeline now runs the
        // monitor first; this is the same-eval coupling the allocator has had since 3.7, deliberately in
        // the same shape rather than a second pattern.
        //
        // The monitor reads no `power_reports`, so the reorder introduces no cycle: it reads accounts,
        // trials, the PRIOR status, strategies and control_equity, and writes status the gate then reads.
        var statusThisEval = db.OverfittingStatus
            .Where(o => o.AsOf == asOf && o.RunKind == runKind)
            .ToDictionary(o => o.StrategyId, o => o.Status, StringComparer.Ordinal);

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

            var maxHorizon = Math.Max(strat.HoldingHorizonDays ?? DefaultHorizonDays, benchHorizon);

            // D118 (v1.9.74) — the effect and the MDE it is judged against come from ONE estimator, at one
            // lag, in one call. Until v1.9.74 this computed `mean(r_s − r_b) × 252`: a RAW ACTIVE-RETURN GAP
            // with no beta term, against D26 ("never a raw return gap") and hard rule 6 (finding 285). The
            // fix is not merely to swap the numerator: judging Jensen's α against the MDE of the β = 1
            // difference series pairs an intercept with the noise of a DIFFERENT estimator and is not a
            // coherent test (finding 345). PairedEffect returns both or neither.
            //
            // A null result is a SKIP — fewer than three observations, or a benchmark with no variation so β
            // is unidentified. Never a zero, never a fallback to the raw gap (rule 10): a pair the arena
            // cannot evaluate must not acquire a verdict by defaulting.
            if (PairedEffect.Compute(stratReturns, benchReturns, PairedEffect.Jensen, maxHorizon, gate)
                is not { } paired) continue;

            var mde = paired.Mde;
            var gap = paired.EffectAnn;
            var verdict = PromotionGate.Decide(gap, mde.MdeAnn, mde.TDays, gate.MinTrackDays);

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
            // The strategies.status MUTATION is forward-only (D37): a replay promotion is recorded in its
            // quarantined go_live_log row — which EffectiveStatus reads as replay-'live' next evaluation —
            // and never reaches the shared column ("replay is never a promotion input").
            // THE SUSPECT VETO (D156). OVERFITTING_MONITOR §3 states it without qualification —
            // "promotion vetoed REGARDLESS OF P&L" — so it is checked here, after the verdict is computed
            // and persisted, and before the promotion acts on it. The power_reports row still records
            // Promoted: the gate's arithmetic DID clear the bar, and rewriting the verdict would destroy
            // the evidence that a vetoed strategy was winning on P&L, which is the only thing that makes
            // the veto worth having. What the veto changes is whether the promotion HAPPENS.
            //
            // A 'retired' status vetoes too, and for a different reason: the monitor auto-retired the
            // strategy in THIS evaluation, so promoting it would resurrect a strategy the arena just
            // killed. Before the reorder this was impossible to express and was instead patched on the
            // monitor's side by writing an offsetting demotion row after the fact.
            //
            // WARNING IS DELIBERATELY NOT HANDLED HERE, AND THAT IS A HOLE UNTIL (b) LANDS. §3 says a
            // Warning permits promotion "only with explicit operator acknowledgment (logged)"; nothing
            // implements that acknowledgment yet, so a Warning strategy still promotes silently — as it
            // did before D156. This is stated rather than left implied, because "the gate now reads the
            // monitor's status" would otherwise read as "the gate now honours every status", and 123 of
            // the frozen generation's 144 promotions were made under Warning. `D156_AWarningStillPromotes_
            // TheAcknowledgmentRailIsNotBuiltYet` pins the hole so the next PR has a red to turn green.
            var monitorStatus = statusThisEval.GetValueOrDefault(strat.StrategyId);
            var vetoed = monitorStatus is "suspect" or "retired";

            if (verdict == PromotionVerdict.Promoted && !vetoed
                && effective.GetValueOrDefault(strat.StrategyId) == "candidate")
            {
                if (runKind == RunKindLive)
                {
                    db.Strategies.First(s => s.StrategyId == strat.StrategyId).Status = "live";
                }
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
