using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Monitor;

/// <summary>One strategy's monitor outcome for a day: the aggregate status + the three signals.</summary>
public readonly record struct MonitorResult(string StrategyId, MonitorStatus Status, SignalOutcome S2, SignalOutcome S3, SignalOutcome S6);

/// <summary>
/// The Phase-3 overfitting monitor (OVERFITTING_MONITOR §3): S2 (deflated Sharpe), S3 (population
/// percentile vs the flat anchors), S6 (rolling edge decay). Persists one overfitting_checks row per
/// signal and one overfitting_status row per strategy per evaluation.
///
/// STATUS IS SIGNAL-WHITELISTED: the aggregate is the max over EXACTLY S2/S3/S6 (the monitor set as it
/// lands). A descriptive row such as signal='turnover_match' (finding 115) is written elsewhere with a
/// neutral contribution and is NEVER passed to <see cref="MonitorSignals.Aggregate"/>, so it cannot move
/// the verdict (FX-TurnoverMatch-StatusNeutral).
///
/// A reader of equity_curve + control_equity; writes via the caller's transaction (D59). Phase-3
/// simplification: all promotable strategies are matched to ONE population per Run call (per-family
/// cadence matching to real strategies arrives later); each member's β-adjusted alpha is computed with a
/// degenerate-safe fallback (a constant-benchmark window falls back to the mean active return).
/// </summary>
public sealed class OverfittingMonitor(AlphaLabDbContext db, GateOptions gate)
{
    public const int RollingWindowDays = 63;                 // Appendix A s6.window_days
    public const int AutoRetireConsecutiveSuspect = 4;       // Appendix A s6.auto_retire_evals
    private const int DefaultLag = 21;                       // NW lag for the alpha t-stat (Buy&Hold-shape default)
    private const string RunKindLive = "live";

    /// <summary>Evaluate + persist S2/S3/S6 + the aggregate status for every promotable strategy against
    /// one matched population.</summary>
    public IReadOnlyList<MonitorResult> Run(string asOf, string benchmarkStrategyId, long? matchedPopulationId, string runKind = RunKindLive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var benchAccount = db.Accounts.FirstOrDefault(a => a.StrategyId == benchmarkStrategyId && a.RunKind == runKind);
        if (benchAccount is null) return [];
        var benchCurve = CurveMath.Curve(db, benchAccount.AccountId, runKind);
        if (benchCurve.Count < 2) return [];

        var (memberAlphas, memberWindowAlphas) = matchedPopulationId is { } pid
            ? PopulationAlphas(pid, benchCurve, runKind)
            : ([], []);

        // The S2 deflation uses the GLOBAL honest trials count (D23 / OVERFITTING_MONITOR §3 + App. B):
        // every fork/sibling/retrain is a new strategy_id, so "one researcher's trial spends everyone's
        // significance" — the same N deflates every strategy's Sharpe. Replay trials (run_kind='replay')
        // are excluded by the predicate. Computed once (it is strategy-invariant).
        var trialsCount = db.TrialsRegistry.Count(t => t.RunKind == runKind);

        var promotable = db.Strategies
            .Where(s => s.Status == "candidate" || s.Status == "live")
            .Select(s => s.StrategyId)
            .ToList();

        var results = new List<MonitorResult>();
        foreach (var strategyId in promotable)
        {
            if (strategyId == benchmarkStrategyId) continue;
            var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == runKind);
            if (account is null) continue;
            var stratCurve = CurveMath.Curve(db, account.AccountId, runKind);
            if (stratCurve.Count < 2) continue;

            var (stratReturns, benchReturns) = CurveMath.AlignedReturns(stratCurve, benchCurve);
            if (stratReturns.Count < 2) continue;

            results.Add(Evaluate(asOf, strategyId, stratReturns, benchReturns, memberAlphas, memberWindowAlphas, trialsCount, runKind));
        }

        return results;
    }

    /// <summary>The pure-ish core: compute the three signals from precomputed inputs, persist the rows,
    /// aggregate over the whitelist, apply the 4-consecutive-Suspect auto-retire, and persist the status.
    /// Saves within the caller's transaction.</summary>
    public MonitorResult Evaluate(
        string asOf, string strategyId,
        IReadOnlyList<double> stratReturns, IReadOnlyList<double> benchReturns,
        IReadOnlyList<double> memberAlphas, IReadOnlyList<double> memberWindowAlphas,
        int trialsCount, string runKind = RunKindLive)
    {
        // S2 — deflated Sharpe.
        var rawSharpe = StrategyMetrics.Sharpe(stratReturns, 0.0);
        var deflated = StrategyMetrics.DeflatedSharpeAnnualized(rawSharpe, stratReturns.Count, Math.Max(1, trialsCount));
        var s2 = MonitorSignals.S2(rawSharpe, deflated);

        // S3 — percentile of the strategy's β-adjusted alpha within the matched population.
        var s3 = memberAlphas.Count > 0
            ? MonitorSignals.S3(Statistics.PercentileRank(memberAlphas, SafeAlpha(stratReturns, benchReturns).Alpha))
            : new SignalOutcome("S3", null, "undefined", MonitorStatus.Healthy);

        // S6 — rolling 63-day alpha t-stat + inside the population's central 50% band.
        SignalOutcome s6;
        if (stratReturns.Count >= RollingWindowDays && memberWindowAlphas.Count > 0)
        {
            var ws = Tail(stratReturns, RollingWindowDays);
            var wb = Tail(benchReturns, RollingWindowDays);
            var window = SafeAlpha(ws, wb);
            var lo = Statistics.Percentile(memberWindowAlphas, 25);
            var hi = Statistics.Percentile(memberWindowAlphas, 75);
            s6 = MonitorSignals.S6(window.T, window.Alpha >= lo && window.Alpha <= hi);
        }
        else
        {
            s6 = new SignalOutcome("S6", null, "insufficient_track", MonitorStatus.Healthy);
        }

        AddCheck(asOf, strategyId, s2, new { elevated_gap_raw_sharpe = MonitorSignals.S2ElevatedGapRawSharpe, raw_sharpe = rawSharpe }, runKind);
        AddCheck(asOf, strategyId, s3, new { healthy_anchor = MonitorSignals.S3HealthyAnchor, suspect_anchor = MonitorSignals.S3SuspectAnchor, n = memberAlphas.Count }, runKind);
        AddCheck(asOf, strategyId, s6, new { window_days = RollingWindowDays, negative_alpha_t = MonitorSignals.S6NegativeAlphaT }, runKind);

        // Aggregate over EXACTLY the monitor signals (the whitelist).
        var aggregate = MonitorSignals.Aggregate([s2.Status, s3.Status, s6.Status]);

        // Auto-retire: four consecutive Suspect evaluations (this one + three priors).
        if (aggregate == MonitorStatus.Suspect &&
            TrailingSuspectCount(strategyId, asOf, runKind) >= AutoRetireConsecutiveSuspect - 1)
        {
            aggregate = MonitorStatus.Retired;
        }

        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            StrategyId = strategyId,
            AsOf = asOf,
            Status = MonitorSignals.ToToken(aggregate),
            TriggerJson = JsonSerializer.Serialize(new { s2 = s2.Contribution, s3 = s3.Contribution, s6 = s6.Contribution }, AlphaLabJson.Options),
            RunKind = runKind,
        });

        if (aggregate == MonitorStatus.Retired)
        {
            var row = db.Strategies.FirstOrDefault(s => s.StrategyId == strategyId);
            if (row is not null) row.Status = "retired";
        }

        db.SaveChanges();
        return new MonitorResult(strategyId, aggregate, s2, s3, s6);
    }

    // ---- helpers ----

    private void AddCheck(string asOf, string strategyId, SignalOutcome sig, object thresholds, string runKind) =>
        db.OverfittingChecks.Add(new OverfittingCheckRow
        {
            StrategyId = strategyId,
            AsOf = asOf,
            Signal = sig.Signal,
            Value = sig.Value,
            ThresholdJson = JsonSerializer.Serialize(thresholds, AlphaLabJson.Options),
            Contribution = sig.Contribution,
            RunKind = runKind,
        });

    private int TrailingSuspectCount(string strategyId, string asOf, string runKind)
    {
        var priors = db.OverfittingStatus
            .Where(o => o.StrategyId == strategyId && o.RunKind == runKind && string.Compare(o.AsOf, asOf) < 0)
            .OrderByDescending(o => o.AsOf)
            .Select(o => o.Status)
            .ToList();

        var count = 0;
        foreach (var s in priors)
        {
            if (s == "suspect") count++;
            else break;
        }
        return count;
    }

    private (List<double> Alphas, List<double> WindowAlphas) PopulationAlphas(long populationId, List<(string AsOf, decimal Equity)> benchCurve, string runKind)
    {
        var members = db.ControlEquity
            .Where(e => e.PopulationId == populationId && e.RunKind == runKind)
            .OrderBy(e => e.MemberIndex).ThenBy(e => e.AsOf)
            .Select(e => new { e.MemberIndex, e.AsOf, e.Equity })
            .AsEnumerable()
            .GroupBy(e => e.MemberIndex);

        var alphas = new List<double>();
        var windowAlphas = new List<double>();
        foreach (var member in members)
        {
            var curve = member.Select(e => (e.AsOf, e.Equity)).ToList();
            var (mr, br) = CurveMath.AlignedReturns(curve, benchCurve);
            if (mr.Count < 2) continue;
            alphas.Add(SafeAlpha(mr, br).Alpha);
            if (mr.Count >= RollingWindowDays)
                windowAlphas.Add(SafeAlpha(Tail(mr, RollingWindowDays), Tail(br, RollingWindowDays)).Alpha);
        }
        return (alphas, windowAlphas);
    }

    // β-adjusted alpha with a degenerate-safe fallback: a constant-benchmark window makes the OLS
    // regressor variation-free (β unidentified), so fall back to the mean active return (β implicitly 1).
    private static (double Alpha, double T) SafeAlpha(IReadOnlyList<double> strat, IReadOnlyList<double> bench)
    {
        try
        {
            var fit = StrategyMetrics.JensenAlpha(strat, bench, 0.0, DefaultLag);
            return (fit.AlphaAnnualized, fit.AlphaTStat);
        }
        catch (ArgumentException)
        {
            double m = 0;
            for (var i = 0; i < strat.Count; i++) m += strat[i] - bench[i];
            return (m / strat.Count * MetricsConstants.TradingDaysPerYear, 0.0);
        }
    }

    private static List<double> Tail(IReadOnlyList<double> xs, int n)
    {
        var start = Math.Max(0, xs.Count - n);
        var tail = new List<double>(xs.Count - start);
        for (var i = start; i < xs.Count; i++) tail.Add(xs[i]);
        return tail;
    }
}
