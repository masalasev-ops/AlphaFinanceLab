using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Monitor;

namespace AlphaLab.Evaluation.Tests;

public class MonitorSignalsTests
{
    [Theory]
    [InlineData(0.6, -0.2, true)]    // deflated < 0 while raw > 0.5 ⇒ elevated (pure selection)
    [InlineData(0.6, 0.1, false)]    // deflated still positive
    [InlineData(0.3, -0.2, false)]   // raw not above 0.5
    public void S2_ElevatedOnlyWhenDeflationFlipsAPositiveSharpe(double raw, double deflated, bool elevated)
    {
        var s = MonitorSignals.S2(raw, deflated);
        Assert.Equal(elevated ? "elevated" : "none", s.Contribution);
        Assert.Equal(elevated ? MonitorStatus.Warning : MonitorStatus.Healthy, s.Status);
    }

    [Theory]
    [InlineData(10.0, "suspect", MonitorStatus.Suspect)]    // < 25th — the anti-predictive tail (D63)
    [InlineData(50.0, "in_band", MonitorStatus.Healthy)]    // median no-edge — NEVER Suspect
    [InlineData(97.0, "healthy", MonitorStatus.Healthy)]    // ≥ 95th — distinguishable above
    [InlineData(25.0, "in_band", MonitorStatus.Healthy)]    // the boundary is not Suspect
    public void S3_FlatAnchors_OnlyTheAntiPredictiveTailIsSuspect(double pct, string contribution, MonitorStatus status)
    {
        var s = MonitorSignals.S3(pct);
        Assert.Equal(contribution, s.Contribution);
        Assert.Equal(status, s.Status);
    }

    [Fact]
    public void S6_NeverSuspectOnASingleEvaluation_EscalatesOnStreaks()
    {
        // D63 still holds under the Phase-4 escalation: a single 63-day window trips t < −1 ~16% of the
        // time under the null, so ONE evaluation is never Suspect. The Appendix-A ladder: a single
        // negative t ⇒ Warning; SUSTAINED (2 consecutive) ⇒ Suspect. Inside-band: one window is normal
        // (none of the ladder), two consecutive ⇒ elevated Warning, three ⇒ critical Suspect.
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(rollingAlphaT: -1.5, insideCentralBand: true).Status);
        Assert.Equal(MonitorStatus.Suspect, MonitorSignals.S6(-1.5, insideCentralBand: true, priorConsecutiveNegativeT: 1).Status);
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.S6(0.2, insideCentralBand: true).Status);
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(0.2, insideCentralBand: true, priorConsecutiveInsideBand: 1).Status);
        Assert.Equal(MonitorStatus.Suspect, MonitorSignals.S6(0.2, insideCentralBand: true, priorConsecutiveInsideBand: 2).Status);
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.S6(0.2, insideCentralBand: false).Status);
        // An outside-band window BREAKS the streak regardless of its depth into it.
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.S6(0.2, insideCentralBand: false, priorConsecutiveInsideBand: 5).Status);
    }

    [Fact]
    public void Aggregate_IsTheMaxSeverity_OverTheWhitelistedSignalsOnly()
    {
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.Aggregate([MonitorStatus.Healthy, MonitorStatus.Healthy, MonitorStatus.Healthy]));
        Assert.Equal(MonitorStatus.Suspect, MonitorSignals.Aggregate([MonitorStatus.Warning, MonitorStatus.Suspect, MonitorStatus.Healthy]));
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.Aggregate([MonitorStatus.Warning, MonitorStatus.Healthy]));
    }
}

public class OverfittingMonitorTests
{
    private static double[] Centroid(IReadOnlyList<double[]> members, int len) =>
        Enumerable.Range(0, len).Select(t => members.Average(m => m[t])).ToArray();

    [Fact]
    public void FX_SyntheticEdge_LandsAbove95thPercentile_Healthy()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        // A strong constant excess above the population centroid ⇒ alpha far above every member.
        var edge = Centroid(members, 99).Select(r => r + 0.003).ToArray();
        arena.SeedStrategy("cand:edge", "candidate", dates, edge);

        using var db = arena.Open();
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:edge");

        Assert.True(result.S3.Value >= 95, $"edge S3 percentile was {result.S3.Value}");
        Assert.Equal(MonitorStatus.Healthy, result.S3.Status);
    }

    [Fact]
    public void FX_SyntheticNoEdge_SitsInBand_NeverSuspect()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        // The population centroid lands at the middle of the alpha distribution — a genuine no-edge draw.
        arena.SeedStrategy("cand:noedge", "candidate", dates, Centroid(members, 99));

        using var db = arena.Open();
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:noedge");

        Assert.True(result.S3.Value >= 25, $"no-edge S3 percentile {result.S3.Value} must not be in the Suspect tail (D63)");
        Assert.True(result.S3.Value < 95, $"no-edge S3 percentile {result.S3.Value} should be in-band, not distinguishable");
        Assert.NotEqual(MonitorStatus.Suspect, result.Status);
    }

    [Fact]
    public void AntiPredictive_LandsInTheSuspectTail()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        // A constant NEGATIVE excess below the centroid ⇒ alpha below the 25th percentile.
        var anti = Centroid(members, 99).Select(r => r - 0.003).ToArray();
        arena.SeedStrategy("cand:anti", "candidate", dates, anti);

        using var db = arena.Open();
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:anti");

        Assert.True(result.S3.Value < 25, $"anti-predictive S3 percentile was {result.S3.Value}");
        Assert.Equal(MonitorStatus.Suspect, result.S3.Status);
        Assert.Equal(MonitorStatus.Suspect, result.Status);
    }

    [Fact]
    public void S2_DeflatesByTheGlobalTrialsCount_ElevatingAModestSharpeUnderManyTrials()
    {
        // D23: the honest trials count is GLOBAL — every fork spends everyone's significance. A modest raw
        // Sharpe (>0.5) selected from many trials deflates negative ⇒ S2 elevated. (This drives the Run/
        // Evaluate trials-count path the isolated MonitorSignals.S2 test never exercised.)
        using var arena = new EvalArena();
        using var db = arena.Open();

        // Centered to an exact daily mean of 0.0007 with ~0.5%/day vol ⇒ raw ann Sharpe ≈ 2.2, comfortably
        // inside (0.5, SR0_ann≈4.7) so the deflation flips it negative under 1000 trials.
        var raw = EvalArena.Noise(120, 0.005, seed: 7);
        var m = raw.Average();
        var returns = raw.Select(x => x - m + 0.0007).ToArray();
        var bench = EvalArena.Noise(120, 0.001, seed: 8);

        var monitor = new OverfittingMonitor(db, new GateOptions());
        var elevated = monitor.Evaluate("2026-03-10", "cand", returns, bench, memberAlphas: [], memberWindowAlphas: [], trialsCount: 1000, runKind: "live");
        Assert.Equal("elevated", elevated.S2.Contribution);
        Assert.Equal(MonitorStatus.Warning, elevated.S2.Status);

        // With a single trial there is no selection to deflate ⇒ the same Sharpe is NOT elevated.
        var notElevated = monitor.Evaluate("2026-03-31", "cand", returns, bench, memberAlphas: [], memberWindowAlphas: [], trialsCount: 1, runKind: "live");
        Assert.Equal("none", notElevated.S2.Contribution);
    }

    [Fact]
    public void AutoRetire_WritesAGoLiveLogDemotionRow()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);
        var anti = Centroid(members, 99).Select(r => r - 0.003).ToArray();   // the anti-predictive Suspect plant
        arena.SeedStrategy("cand:anti", "candidate", dates, anti);

        using var db = arena.Open();
        // Three PRIOR consecutive Suspect evaluations — this one makes four ⇒ auto-retire.
        foreach (var d in new[] { "2026-01-01", "2026-01-02", "2026-01-03" })
            db.OverfittingStatus.Add(new OverfittingStatusRow { StrategyId = "cand:anti", AsOf = d, Status = "suspect", TriggerJson = "{}", RunKind = "live" });
        db.SaveChanges();

        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:anti");

        Assert.Equal(MonitorStatus.Retired, result.Status);
        Assert.Equal("retired", db.Strategies.Single(s => s.StrategyId == "cand:anti").Status);
        // The retire is audited as a demotion EVENT (D31) — the go_live_log's `demoted` column, verdict 'Revert'.
        var demotion = Assert.Single(db.GoLiveLog.Where(g => g.Demoted == "cand:anti").ToList());
        Assert.Null(demotion.Promoted);
        Assert.Equal("Revert", demotion.Verdict);
        Assert.Contains("auto_retire", demotion.EvidenceJson);
    }

    [Fact]
    public void S3_HorizonMatchesMemberAlphasToTheStrategyTrackLength_NotTheFullMemberTrack()
    {
        // A YOUNG strategy is scored against member alphas computed over ITS window length, not the members'
        // full track. Members up-drift strongly over their first 69 days then go flat; a young flat strategy
        // (last 30 days only) must be ranked against the members' FLAT last-30 window (~50th, in-band), not
        // their up-drifting full track (which would slam it into the <25th Suspect tail).
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());   // flat benchmark

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i =>
        {
            var r = new double[99];
            for (var t = 0; t < 99; t++)
                r[t] = t < 69 ? 0.003 + i * 0.0001         // dispersed strong up-drift over the first 69 days
                              : (i - m / 2) * 0.00002;      // tiny dispersed ~zero-mean returns over the last 30
            return r;
        }).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        var youngDates = dates.Skip(dates.Count - 31).ToList();   // 31 points ⇒ 30 returns (the last 30 sessions)
        arena.SeedStrategy("cand:young", "candidate", youngDates, Enumerable.Repeat(0.0, 30).ToArray());   // flat ⇒ ~0 alpha

        using var db = arena.Open();
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:young");

        Assert.True(result.S3.Value >= 25, $"young S3 percentile {result.S3.Value} should be in-band once horizon-matched, not in the Suspect tail");
    }

    [Fact]
    public void Run_PersistsThreeCheckRowsPerStrategy_AndOneStatusRow()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(80, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(79, 0.008, seed: 5));
        arena.SeedStrategy("cand:a", "candidate", dates, EvalArena.Noise(79, 0.01, seed: 9));
        var popId = arena.SeedPopulation("daily", true, 1001, dates,
            i => EvalArena.Noise(79, 0.01, 3000 + i), m: 30);

        using var db = arena.Open();
        new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId);

        var checks = db.OverfittingChecks.Where(c => c.StrategyId == "cand:a").Select(c => c.Signal).OrderBy(s => s).ToList();
        Assert.Equal(["S2", "S3", "S6"], checks);
        Assert.Single(db.OverfittingStatus.Where(o => o.StrategyId == "cand:a"));
        Assert.All(db.OverfittingChecks.ToList(), c => Assert.Equal("live", c.RunKind));
    }
}
