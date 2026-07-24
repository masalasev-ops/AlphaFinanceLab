using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
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

    /// <summary>Change 1 (two-pass calibration): the go_live_log verdict a would-be plant retire carries
    /// when the calibration replay exempts it from actually retiring. Distinct from 'Revert' (a real
    /// demotion) so ReplayVerification's would-be-survival KPI reads exactly these, never a live retire.</summary>
    public const string WouldRevertVerdict = "WouldRevert";
    private const int DefaultLag = 21;                       // NW lag for the alpha t-stat (Buy&Hold-shape default)
    private const string RunKindLive = "live";

    /// <summary>Evaluate + persist S2/S3/S6 + the aggregate status for every promotable strategy against
    /// one matched population. <paramref name="watermark"/> resolves the CALIBRATED config rows as-of
    /// the run (D96/D98): when the frozen D56 curves exist at that watermark, S3 judges against the
    /// trajectory (S3Trajectory); otherwise the flat pre-calibration anchors apply — behaviour-preserving
    /// until the Phase-4 calibration writes the rows. The S6 auto-retire patience resolves the same way.</summary>
    public IReadOnlyList<MonitorResult> Run(
        string asOf, string benchmarkStrategyId, long? matchedPopulationId, string runKind = RunKindLive,
        string? watermark = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var (pNoise, pEdge) = LoadCurves("daily", watermark);
        var autoRetireEvals = ResolveAutoRetirePatience(watermark);

        var benchAccount = db.Accounts.FirstOrDefault(a => a.StrategyId == benchmarkStrategyId && a.RunKind == runKind);
        if (benchAccount is null) return [];
        var benchCurve = CurveMath.Curve(db, benchAccount.AccountId, runKind);
        if (benchCurve.Count < 2) return [];

        // The matched population's per-member aligned return series (vs the benchmark). Kept as SERIES, not
        // pre-reduced alphas, so S3 can be horizon-matched to each strategy's own track length below.
        var memberSeries = matchedPopulationId is { } pid
            ? PopulationReturns(pid, benchCurve, runKind)
            : [];

        // The S2 deflation uses the GLOBAL honest trials count (D23 / OVERFITTING_MONITOR §3 + App. B):
        // every fork/sibling/retrain is a new strategy_id, so "one researcher's trial spends everyone's
        // significance" — the same N deflates every strategy's Sharpe. Replay trials (run_kind='replay')
        // are excluded by the predicate. Computed once (it is strategy-invariant).
        var trialsCount = db.TrialsRegistry.Count(t => t.RunKind == runKind);

        // Run-kind-scoped status (Phase 4/D37): a replay-retired strategy drops out of the REPLAY
        // promotable set via its own quarantined records, never via the shared forward column.
        var effective = EffectiveStatus.Resolve(db, runKind);
        var promotable = effective
            .Where(kv => kv.Value is "candidate" or "live")
            .Select(kv => kv.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
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

            // Horizon-match S3 to THIS strategy's track: rank its alpha inside member alphas computed over
            // the SAME window length (the strategy's return count), not the members' full ~200-day tracks.
            // A young strategy's short-window alpha has far higher sampling variance, so scoring it against a
            // tighter long-window distribution over-trips the <25th Suspect tail. S6 already tail-windows both
            // sides to 63 days; S3 was the unmatched signal. For a mature strategy (L ≥ member length) the
            // tail is the full series, so this leaves established comparisons unchanged.
            var l = stratReturns.Count;
            var memberAlphas = memberSeries
                .Select(ms => (Mr: Tail(ms.Mr, l), Br: Tail(ms.Br, l)))
                .Where(w => w.Mr.Count >= 2)
                .Select(w => SafeAlpha(w.Mr, w.Br).Alpha)
                .ToList();
            var memberWindowAlphas = memberSeries
                .Where(ms => ms.Mr.Count >= RollingWindowDays)
                .Select(ms => SafeAlpha(Tail(ms.Mr, RollingWindowDays), Tail(ms.Br, RollingWindowDays)).Alpha)
                .ToList();

            results.Add(Evaluate(asOf, strategyId, stratReturns, benchReturns, memberAlphas, memberWindowAlphas,
                trialsCount, runKind, pNoise, pEdge, autoRetireEvals));
        }

        return results;
    }

    /// <summary>The pure-ish core: compute the three signals from precomputed inputs, persist the rows,
    /// aggregate over the whitelist, apply the consecutive-Suspect auto-retire at the CALIBRATED
    /// patience (<paramref name="autoRetireEvals"/>; Appendix-A default 4), and persist the status.
    /// Saves within the caller's transaction.</summary>
    public MonitorResult Evaluate(
        string asOf, string strategyId,
        IReadOnlyList<double> stratReturns, IReadOnlyList<double> benchReturns,
        IReadOnlyList<double> memberAlphas, IReadOnlyList<double> memberWindowAlphas,
        int trialsCount, string runKind = RunKindLive,
        Calibration.S3Curve? pNoise = null, Calibration.S3Curve? pEdge = null,
        int autoRetireEvals = AutoRetireConsecutiveSuspect)
    {
        // S2 — deflated Sharpe.
        var rawSharpe = StrategyMetrics.Sharpe(stratReturns, 0.0);
        var deflated = StrategyMetrics.DeflatedSharpeAnnualized(rawSharpe, stratReturns.Count, Math.Max(1, trialsCount));
        var s2 = MonitorSignals.S2(rawSharpe, deflated);

        // S3 — percentile of the strategy's β-adjusted alpha within the matched population. With the
        // frozen D56 curves present (D98 rows at this run's watermark), the TRAJECTORY judges at this
        // strategy's own track length; the flat anchors are the pre-calibration fallback only.
        SignalOutcome s3;
        object s3Thresholds;
        if (memberAlphas.Count == 0)
        {
            s3 = new SignalOutcome("S3", null, "undefined", MonitorStatus.Healthy);
            s3Thresholds = new { undefined = true, n = 0 };
        }
        else if (pNoise is not null && pEdge is not null)
        {
            var percentile = Statistics.PercentileRank(memberAlphas, SafeAlpha(stratReturns, benchReturns).Alpha);
            var trackDays = stratReturns.Count;
            var priorBelow = TrailingStreak(strategyId, "S3", asOf, runKind, MonitorSignals.ContinuesBelowNoiseStreak);
            s3 = MonitorSignals.S3Trajectory(percentile, trackDays, pNoise.At(trackDays), pEdge.At(trackDays), priorBelow, pNoise.SustainEvals);
            s3Thresholds = new
            {
                p_noise_at = pNoise.At(trackDays), p_edge_at = pEdge.At(trackDays),
                track_days = trackDays, sustain_evals = pNoise.SustainEvals, n = memberAlphas.Count,
            };
        }
        else
        {
            // Flat pre-calibration anchors. Change 3 (D63): a sub-25th dip is Suspect only when SUSTAINED —
            // the persisted below-anchor streak (this eval included) must reach FlatAnchorSustainEvals, so a
            // no-edge plant's rare within-null dip is a Warning, never a Suspect that could retire it.
            var percentile = Statistics.PercentileRank(memberAlphas, SafeAlpha(stratReturns, benchReturns).Alpha);
            var priorBelowAnchor = TrailingStreak(strategyId, "S3", asOf, runKind, MonitorSignals.ContinuesBelowAnchorStreak);
            s3 = MonitorSignals.S3(percentile, priorBelowAnchor, MonitorSignals.FlatAnchorSustainEvals);
            s3Thresholds = new
            {
                healthy_anchor = MonitorSignals.S3HealthyAnchor, suspect_anchor = MonitorSignals.S3SuspectAnchor,
                sustain_evals = MonitorSignals.FlatAnchorSustainEvals, n = memberAlphas.Count,
            };
        }

        // S6 — rolling 63-day alpha t-stat + inside the population's central 50% band, with the
        // Appendix-A escalation streaks (this evaluation + the persisted priors).
        SignalOutcome s6;
        if (stratReturns.Count >= RollingWindowDays && memberWindowAlphas.Count > 0)
        {
            var ws = Tail(stratReturns, RollingWindowDays);
            var wb = Tail(benchReturns, RollingWindowDays);
            var window = SafeAlpha(ws, wb);
            var lo = Statistics.Percentile(memberWindowAlphas, 25);
            var hi = Statistics.Percentile(memberWindowAlphas, 75);
            var priorInside = TrailingStreak(strategyId, "S6", asOf, runKind, MonitorSignals.ContinuesInsideBandStreak);
            var priorNegative = TrailingStreak(strategyId, "S6", asOf, runKind, MonitorSignals.ContinuesNegativeTStreak);
            s6 = MonitorSignals.S6(window.T, window.Alpha >= lo && window.Alpha <= hi, priorInside, priorNegative);
        }
        else
        {
            s6 = new SignalOutcome("S6", null, "insufficient_track", MonitorStatus.Healthy);
        }

        AddCheck(asOf, strategyId, s2, new { elevated_gap_raw_sharpe = MonitorSignals.S2ElevatedGapRawSharpe, raw_sharpe = rawSharpe }, runKind);
        AddCheck(asOf, strategyId, s3, s3Thresholds, runKind);
        AddCheck(asOf, strategyId, s6, new { window_days = RollingWindowDays, negative_alpha_t = MonitorSignals.S6NegativeAlphaT }, runKind);

        // Aggregate over EXACTLY the monitor signals (the whitelist).
        var aggregate = MonitorSignals.Aggregate([s2.Status, s3.Status, s6.Status]);

        // Auto-retire: the sustained-Suspect streak at the CALIBRATED patience (finding 113: a survival-
        // floor failure recalibrates this value — via a new Monitor.S6.AutoRetireEvals config version —
        // never the plant).
        var wouldRetire = aggregate == MonitorStatus.Suspect &&
            TrailingSuspectCount(strategyId, asOf, runKind) >= autoRetireEvals - 1;

        // Change 1 — two-pass calibration (the B2 core fix). During a CALIBRATION replay the D56 curves do
        // not exist yet, so the monitor runs on the flat PRE-calibration anchors + S6 escalation: its
        // verdicts are uncalibrated BY CONSTRUCTION. ACTING on them (retiring a D64 plant) is the category
        // error — it (a) truncates the S3 trajectory the curves are BUILT from (a retired plant leaves the
        // promotable set and stops emitting S3 rows ⇒ the curves collapse to survivors) and (b) makes the
        // verification KPIs, read from that flat-anchor status, hard-FAIL. So a PLANT under replay is never
        // flipped to Retired. The would-be retire is still RECORDED below (amendment A2), so finding 113's
        // audit ("every edge-plant auto-retire logged with its triggering signal") and the would-be-survival
        // KPI (Change 2) stay honest — the fix stops ACTING on the verdicts, it does not stop recording them.
        var exemptFromRetire = runKind != RunKindLive && Calibration.PlantCohorts.IsPlantId(strategyId);
        if (wouldRetire && !exemptFromRetire)
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
            // The shared-column mutation is FORWARD-only (D37): a replay auto-retire is fully recorded
            // by its quarantined overfitting_status 'retired' row (which EffectiveStatus reads) + the
            // go_live_log demotion below — it must never flip the forward strategy to 'retired'.
            if (runKind == RunKindLive)
            {
                var row = db.Strategies.FirstOrDefault(s => s.StrategyId == strategyId);
                if (row is not null) row.Status = "retired";
            }

            // The retire is a demotion EVENT in the go-live/retire audit (D31): write the go_live_log row
            // with the dedicated `demoted` column set (verdict 'Revert'). Without it the audit records only
            // promotions and a retired strategy still reads as live/promoted there — in the same-eval case
            // (EvaluationStep promotes a candidate, then the monitor retires it) the log would carry a
            // Promoted row with no offsetting demotion. A retired strategy is not re-evaluated (it drops out
            // of the promotable set), so exactly one demotion row is written.
            db.GoLiveLog.Add(new GoLiveLogRow
            {
                AsOf = asOf,
                Promoted = null,
                Demoted = strategyId,
                Verdict = "Revert",
                // The trigger records the patience ACTUALLY applied (Phase-4 review): the threshold is
                // the calibrated Monitor.S6.AutoRetireEvals, so a hardcoded "four_consecutive_suspect"
                // would misstate the rule in the immutable audit the moment the operator recalibrates.
                EvidenceJson = JsonSerializer.Serialize(
                    new
                    {
                        reason = "auto_retire",
                        trigger = "consecutive_suspect",
                        consecutive_suspect_evals = autoRetireEvals,
                        s2 = s2.Contribution, s3 = s3.Contribution, s6 = s6.Contribution,
                    },
                    AlphaLabJson.Options),
                RunKind = runKind,
            });
        }
        else if (wouldRetire && exemptFromRetire)
        {
            // Change 1 / amendment A2 — the plant was EXEMPTED from retirement (it stays 'suspect', stays in
            // the promotable set, keeps emitting S3 rows for the full window), but the would-be retire is
            // recorded here so finding 113's mandated audit trail survives and Change 2's would-be-survival
            // KPI has an event to read. A distinct 'WouldRevert' verdict (go_live_log.verdict has no CHECK)
            // keeps it separable from a real demotion; no strategies-column flip. The trigger records the
            // patience actually applied + the S2/S3/S6 contributions — the SAME triggering-signal evidence a
            // real auto-retire carries, minus the actual state change.
            db.GoLiveLog.Add(new GoLiveLogRow
            {
                AsOf = asOf,
                Promoted = null,
                Demoted = strategyId,
                Verdict = WouldRevertVerdict,
                EvidenceJson = JsonSerializer.Serialize(
                    new
                    {
                        reason = "would_auto_retire",
                        trigger = "consecutive_suspect",
                        consecutive_suspect_evals = autoRetireEvals,
                        s2 = s2.Contribution, s3 = s3.Contribution, s6 = s6.Contribution,
                    },
                    AlphaLabJson.Options),
                RunKind = runKind,
            });
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

    // The frozen D56 curves as-of the run's watermark (D96/D98). Both must exist to switch S3 to the
    // trajectory — a half-frozen pair falls back to the flat anchors (fail toward the pre-calibration
    // behaviour, never a curve judged against a flat opposite).
    private (Calibration.S3Curve? Noise, Calibration.S3Curve? Edge) LoadCurves(string family, string? watermark)
    {
        var config = new ConfigReadService(db);
        string? Read(string key) => watermark is null ? config.ResolveCurrent(key) : config.ResolveAsOf(key, watermark);
        var noiseJson = Read(Calibration.CalibratedKeys.PNoiseCurve(family));
        var edgeJson = Read(Calibration.CalibratedKeys.PEdgeCurve(family));
        if (noiseJson is null || edgeJson is null) return (null, null);
        return (Calibration.S3Curve.FromJson(noiseJson), Calibration.S3Curve.FromJson(edgeJson));
    }

    private int ResolveAutoRetirePatience(string? watermark)
    {
        var config = new ConfigReadService(db);
        var raw = watermark is null
            ? config.ResolveCurrent(Calibration.CalibratedKeys.S6AutoRetireEvals)
            : config.ResolveAsOf(Calibration.CalibratedKeys.S6AutoRetireEvals, watermark);
        return raw is not null && int.TryParse(raw, out var v) && v >= 2 ? v : AutoRetireConsecutiveSuspect;
    }

    // How many consecutive PRIOR evaluations of one signal continued a streak (matched by contribution
    // token), most recent first — the persisted-path form of "sustained" (NFR-2: reconstructible).
    private int TrailingStreak(string strategyId, string signal, string asOf, string runKind, Func<string, bool> continues)
    {
        var priors = db.OverfittingChecks
            .Where(c => c.StrategyId == strategyId && c.Signal == signal && c.RunKind == runKind
                        && string.Compare(c.AsOf, asOf) < 0)
            .OrderByDescending(c => c.AsOf)
            .Select(c => c.Contribution)
            .ToList();

        var count = 0;
        foreach (var token in priors)
        {
            if (continues(token)) count++;
            else break;
        }
        return count;
    }

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

    // Each population member's aligned (member, benchmark) return series over the forward window. Returned
    // as SERIES (not reduced to a single alpha) so the caller can window them to each strategy's track
    // length for the horizon-matched S3 rank.
    private List<(List<double> Mr, List<double> Br)> PopulationReturns(long populationId, List<(string AsOf, decimal Equity)> benchCurve, string runKind)
    {
        var members = db.ControlEquity
            .Where(e => e.PopulationId == populationId && e.RunKind == runKind)
            .OrderBy(e => e.MemberIndex).ThenBy(e => e.AsOf)
            .Select(e => new { e.MemberIndex, e.AsOf, e.Equity })
            .AsEnumerable()
            .GroupBy(e => e.MemberIndex);

        var series = new List<(List<double> Mr, List<double> Br)>();
        foreach (var member in members)
        {
            var curve = member.Select(e => (e.AsOf, e.Equity)).ToList();
            var (mr, br) = CurveMath.AlignedReturns(curve, benchCurve);
            if (mr.Count < 2) continue;
            series.Add((mr, br));
        }
        return series;
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
