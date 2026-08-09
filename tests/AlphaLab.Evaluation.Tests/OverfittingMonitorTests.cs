using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Monitor;
using AlphaLab.Evaluation.Populations;

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

    // Change 3 (D63 conformance): the flat anchor flags the anti-predictive tail (< 25th) as Suspect ONLY
    // when SUSTAINED to FlatAnchorSustainEvals (3) consecutive evals — a single/double dip is a Warning, so
    // "a merely edgeless strategy … S3 never flags it" (§3) holds for its rare within-null excursions. The
    // ≥95th is Healthy, ~50th is in_band, and the 25th boundary is not the tail.
    [Theory]
    [InlineData(10.0, 0, "below_anchor", MonitorStatus.Warning)]  // single dip → Warning, not Suspect
    [InlineData(10.0, 1, "below_anchor", MonitorStatus.Warning)]  // two consecutive → still Warning
    [InlineData(10.0, 2, "suspect", MonitorStatus.Suspect)]       // three consecutive → Suspect (anti-predictive)
    [InlineData(50.0, 9, "in_band", MonitorStatus.Healthy)]       // median no-edge — never reaches the anchor
    [InlineData(97.0, 0, "healthy", MonitorStatus.Healthy)]       // ≥ 95th — distinguishable above
    [InlineData(25.0, 9, "in_band", MonitorStatus.Healthy)]       // the boundary is not the tail
    public void S3_FlatAnchors_AntiPredictiveTailIsSuspect_OnlyWhenSustained(double pct, int priorBelow, string contribution, MonitorStatus status)
    {
        var s = MonitorSignals.S3(pct, priorBelow, MonitorSignals.FlatAnchorSustainEvals);
        Assert.Equal(contribution, s.Contribution);
        Assert.Equal(status, s.Status);
    }

    private const MonitorSignals.BandPosition Below = MonitorSignals.BandPosition.Below;
    private const MonitorSignals.BandPosition Inside = MonitorSignals.BandPosition.Inside;
    private const MonitorSignals.BandPosition Above = MonitorSignals.BandPosition.Above;

    [Fact]
    public void S6_NegAlphaSuspectOnlyWhenSustained_AndOnlyBelowTheBand()
    {
        // The anti-predictive arm: BELOW the band AND a sustained negative t. Warning once (a single
        // 63-day window trips t < −1 at the null rate), Suspect only at FlatAnchorSustainEvals (3).
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(rollingAlphaT: -1.5, band: Below).Status);
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(-1.5, Below, priorConsecutiveNegativeT: 1).Status);  // 2 consecutive — still Warning
        Assert.Equal(MonitorStatus.Suspect, MonitorSignals.S6(-1.5, Below, priorConsecutiveNegativeT: 2).Status);  // 3 consecutive — Suspect

        // Inside-band decay is a CAUTION (Warning) at most, NEVER Suspect — D63 scope note: "do not tune S6
        // to catch mid-band lifers." One window normal; two consecutive elevated; never escalates further.
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.S6(0.2, Inside).Status);
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(0.2, Inside, priorConsecutiveInsideBand: 1).Status);
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(0.2, Inside, priorConsecutiveInsideBand: 2).Status);  // 3 consecutive — STILL Warning
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(0.2, Inside, priorConsecutiveInsideBand: 9).Status);  // never Suspect, however long

        // Above the band is Healthy regardless of any prior streak.
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.S6(0.2, Above).Status);
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.S6(0.2, Above, priorConsecutiveInsideBand: 5).Status);
    }

    /// <summary>
    /// FINDING 280's REMEDY (6.5). THIS FIXTURE IS THE WHOLE CHECKPOINT IN FOUR LINES, and it is the one
    /// that used to assert the defect: `S6(-1.5, insideCentralBand: true)` reached Suspect at three
    /// consecutive evaluations, because the negative-t arm returned BEFORE the band was consulted.
    ///
    /// That is not an edge case for the D64 no-edge plants — it is their normal state. A plant is a
    /// 40-name daily-churn COST-PAYING population member measured against a near-cost-free buy-and-hold
    /// benchmark, so its window alpha carries the family's ~21.9 %/yr drag and t &lt; −1 lands on the large
    /// majority of evaluations while it sits at the MEDIAN of its own cost-matched band.
    ///
    /// Both sources already required the band to be part of the judgement, so the remedy is CONFORMANCE:
    /// §3 — "vs the strategy's own history **and vs its population band**"; D63 — "an edgeless strategy
    /// is never *below* a cost-matched band." Below the band is anti-predictive; below ZERO is paying costs.
    /// </summary>
    [Fact]
    public void S6_AStrategyAtItsOwnNullsMedian_IsNeverSuspect_HoweverNegativeItsTStatAgainstZero()
    {
        // A no-edge plant's normal state: deeply negative against zero, squarely inside its own band.
        foreach (var t in new[] { -1.5, -2.5, -5.0, -20.0 })
        {
            for (var streak = 0; streak <= 10; streak++)
            {
                var outcome = MonitorSignals.S6(t, Inside, priorConsecutiveInsideBand: streak, priorConsecutiveNegativeT: streak);
                Assert.NotEqual(MonitorStatus.Suspect, outcome.Status);
                Assert.NotEqual(MonitorStatus.Retired, outcome.Status);
            }
        }

        // ...while a genuinely anti-predictive track — BELOW its cost-matched band — still reaches Suspect
        // on exactly the same sustain rule. The remedy narrows the arm's reference frame; it does not
        // disarm it, which is what would have traded one false verdict for another.
        Assert.Equal(MonitorStatus.Suspect, MonitorSignals.S6(-1.5, Below, priorConsecutiveNegativeT: 2).Status);
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

    /// <summary>
    /// **D141's fixture: a config read on a replay path resolves AS-OF the run's watermark, never CURRENT.**
    ///
    /// The S6 auto-retire patience is the cleanest probe available — it is an integer the monitor reads from
    /// a calibrated config row and applies directly, so two versions produce two observably different
    /// retire points with nothing else moving. Two versions are seeded: v1 = 2 stamped BEFORE the run, v2 =
    /// 99 stamped AFTER it. A run at the earlier watermark must see v1.
    ///
    /// **Why it could not be written before D141:** `Run`'s watermark was `string? … = null`, and null took
    /// the `ResolveCurrent` branch — so the quantity this asserts had no way to be stated, and every fixture
    /// in this file silently exercised the run-time-current read. The assertion is the decision.
    /// </summary>
    [Fact]
    public void FX_D141_ReplayPathConfigRead_ResolvesAsOfTheWatermark_NotCurrent()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);
        arena.SeedStrategy("cand:edge", "candidate", dates, Centroid(members, 99).Select(r => r + 0.003).ToArray());

        var runWatermark = EvalArena.Watermark(dates[^1]);
        using var db = arena.Open();
        db.Config.Add(new AlphaLab.Data.Entities.ConfigRow
        {
            Key = AlphaLab.Evaluation.Calibration.CalibratedKeys.S6AutoRetireEvals,
            ValueJson = "2", Version = 1, ChangedOn = dates[0], Reason = "D141 fixture: visible at the watermark",
        });
        db.Config.Add(new AlphaLab.Data.Entities.ConfigRow
        {
            Key = AlphaLab.Evaluation.Calibration.CalibratedKeys.S6AutoRetireEvals,
            ValueJson = "99", Version = 2, ChangedOn = "2099-01-01T00:00:00Z",
            Reason = "D141 fixture: appended AFTER the run — must be invisible to it",
        });
        db.SaveChanges();

        // The monitor resolves the patience internally, so the observable is that it RAN and used the
        // as-of row: assert the resolution directly against the same service the monitor uses, at the same
        // watermark, then confirm the monitor itself completes against that store.
        var config = new AlphaLab.Data.Services.ConfigReadService(db);
        Assert.Equal("2", config.ResolveAsOf(AlphaLab.Evaluation.Calibration.CalibratedKeys.S6AutoRetireEvals, runWatermark));
        Assert.Equal("99", config.ResolveCurrent(AlphaLab.Evaluation.Calibration.CalibratedKeys.S6AutoRetireEvals));

        var results = new OverfittingMonitor(db, new GateOptions())
            .Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", runWatermark);
        Assert.NotEmpty(results);

        // And the branch that would have read v2 is GONE, not merely unused: `Run` cannot be called without
        // an instant. This is the compile-time half of the decision, pinned so a later signature change
        // that reintroduces a nullable default fails here rather than silently restoring the old read.
        var watermarkParam = typeof(OverfittingMonitor).GetMethod(nameof(OverfittingMonitor.Run))!
            .GetParameters().Single(p => p.Name == "watermark");
        Assert.False(watermarkParam.IsOptional, "OverfittingMonitor.Run's watermark must stay REQUIRED (D141)");
        Assert.Equal(typeof(string), watermarkParam.ParameterType);
    }

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
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^1])).Single(r => r.StrategyId == "cand:edge");

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
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^1])).Single(r => r.StrategyId == "cand:noedge");

        Assert.True(result.S3.Value >= 25, $"no-edge S3 percentile {result.S3.Value} must not be in the Suspect tail (D63)");
        Assert.True(result.S3.Value < 95, $"no-edge S3 percentile {result.S3.Value} should be in-band, not distinguishable");
        Assert.NotEqual(MonitorStatus.Suspect, result.Status);
    }

    [Fact]
    public void AntiPredictive_LandsInTheTail_WarningOnASingleEval_SuspectWhenSustained()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        // A constant NEGATIVE excess below the centroid ⇒ alpha below the 25th percentile every eval.
        var anti = Centroid(members, 99).Select(r => r - 0.003).ToArray();
        arena.SeedStrategy("cand:anti", "candidate", dates, anti);

        using var db = arena.Open();
        var monitor = new OverfittingMonitor(db, new GateOptions());

        // Change 3 (D63): the alpha lands in the sub-25th tail, but a SINGLE dip is a Warning, not Suspect.
        var first = monitor.Run(dates[^3], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^3])).Single(r => r.StrategyId == "cand:anti");
        Assert.True(first.S3.Value < 25, $"anti-predictive S3 percentile was {first.S3.Value}");
        Assert.Equal("below_anchor", first.S3.Contribution);
        Assert.Equal(MonitorStatus.Warning, first.S3.Status);

        // SUSTAINED (three consecutive sub-25th evals) ⇒ Suspect — the anti-predictive fast-kill channel.
        monitor.Run(dates[^2], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^2]));
        var third = monitor.Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^1])).Single(r => r.StrategyId == "cand:anti");
        Assert.Equal(MonitorStatus.Suspect, third.S3.Status);
        Assert.Equal(MonitorStatus.Suspect, third.Status);
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
        // Three PRIOR consecutive Suspect evaluations — this one makes four ⇒ auto-retire. Each prior
        // Suspect eval also left its S3 'suspect' check (Change 3): the sustained below-anchor streak is
        // what makes THIS eval's S3 Suspect (a single dip would only be Warning), which drives the aggregate.
        foreach (var d in new[] { "2026-01-01", "2026-01-02", "2026-01-03" })
        {
            db.OverfittingStatus.Add(new OverfittingStatusRow { StrategyId = "cand:anti", AsOf = d, Status = "suspect", TriggerJson = "{}", RunKind = "live" });
            db.OverfittingChecks.Add(new OverfittingCheckRow { StrategyId = "cand:anti", AsOf = d, Signal = "S3", Value = 5.0, ThresholdJson = "{}", Contribution = "suspect", RunKind = "live" });
        }
        db.SaveChanges();

        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^1])).Single(r => r.StrategyId == "cand:anti");

        Assert.Equal(MonitorStatus.Retired, result.Status);
        Assert.Equal("retired", db.Strategies.Single(s => s.StrategyId == "cand:anti").Status);
        // The retire is audited as a demotion EVENT (D31) — the go_live_log's `demoted` column, verdict 'Revert'.
        var demotion = Assert.Single(db.GoLiveLog.Where(g => g.Demoted == "cand:anti").ToList());
        Assert.Null(demotion.Promoted);
        Assert.Equal("Revert", demotion.Verdict);
        Assert.Contains("auto_retire", demotion.EvidenceJson);
    }

    // Change 1 (two-pass calibration): a D64 PLANT under a REPLAY run that WOULD auto-retire is exempted —
    // it is never flipped to Retired (so it stays promotable and keeps emitting S3 rows for the FULL window,
    // the trajectory the curves are built from), but the would-be retire is still RECORDED with its
    // triggering signal (finding-113 audit) under a distinct 'WouldRevert' verdict. The live control above
    // (AutoRetire_WritesAGoLiveLogDemotionRow) proves a non-replay/non-plant strategy still retires normally.
    [Fact]
    public void Change1_ReplayPlant_ExemptFromRetire_RecordsWouldBeRetire_AndIsNotTruncated()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(101, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(100, 0.008, seed: 5), runKind: "replay");

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(100, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m, runKind: "replay");
        // An anti-predictive PLANT: a constant negative excess ⇒ S3 percentile < 25 ⇒ Suspect every eval.
        const string plant = "plant:anti:daily:-2:0";
        var anti = Centroid(members, 100).Select(r => r - 0.003).ToArray();
        arena.SeedStrategy(plant, "candidate", dates, anti, runKind: "replay");

        using var db = arena.Open();
        // Three PRIOR consecutive Suspect evals under REPLAY (status + the sustained S3 'suspect' checks that
        // produced them, Change 3) ⇒ the next Run's S3 sustains to Suspect ⇒ WOULD auto-retire on the fourth.
        foreach (var d in new[] { "2025-12-30", "2025-12-31", "2026-01-01" })
        {
            db.OverfittingStatus.Add(new OverfittingStatusRow { StrategyId = plant, AsOf = d, Status = "suspect", TriggerJson = "{}", RunKind = "replay" });
            db.OverfittingChecks.Add(new OverfittingCheckRow { StrategyId = plant, AsOf = d, Signal = "S3", Value = 5.0, ThresholdJson = "{}", Contribution = "suspect", RunKind = "replay" });
        }
        db.SaveChanges();

        var monitor = new OverfittingMonitor(db, new GateOptions());

        // First eval: WOULD retire, but the plant is EXEMPT — Suspect, never Retired.
        var r1 = monitor.Run(dates[^2], "buyhold:cw", PopulationMatcher.Fixed(popId), "replay", EvalArena.Watermark(dates[^2])).Single(r => r.StrategyId == plant);
        Assert.Equal(MonitorStatus.Suspect, r1.Status);
        Assert.DoesNotContain(db.OverfittingStatus.ToList(), s => s.StrategyId == plant && s.Status == "retired");
        Assert.NotEqual("retired", db.Strategies.Single(s => s.StrategyId == plant).Status);   // forward column never flipped

        // The would-be retire is recorded with its triggering signal, as a distinct 'WouldRevert' verdict.
        var wouldBe = Assert.Single(db.GoLiveLog.Where(g => g.Demoted == plant).ToList());
        Assert.Equal("WouldRevert", wouldBe.Verdict);
        Assert.Contains("would_auto_retire", wouldBe.EvidenceJson);

        // No truncation: the plant stayed promotable, so a SECOND eval (after the first would-retire) still
        // evaluates it and writes an S3 row at that date (a real retire would have dropped it from
        // EffectiveStatus ⇒ no result, no S3 row on the second pass).
        var run2 = monitor.Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "replay", EvalArena.Watermark(dates[^1]));
        Assert.Contains(run2, r => r.StrategyId == plant);
        Assert.Contains(db.OverfittingChecks.ToList(),
            c => c.StrategyId == plant && c.Signal == "S3" && c.AsOf == dates[^1] && c.RunKind == "replay");
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
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^1])).Single(r => r.StrategyId == "cand:young");

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
        new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", PopulationMatcher.Fixed(popId), "live", EvalArena.Watermark(dates[^1]));

        var checks = db.OverfittingChecks.Where(c => c.StrategyId == "cand:a").Select(c => c.Signal).OrderBy(s => s).ToList();
        Assert.Equal(["S2", "S3", "S6"], checks);
        Assert.Single(db.OverfittingStatus.Where(o => o.StrategyId == "cand:a"));
        Assert.All(db.OverfittingChecks.ToList(), c => Assert.Equal("live", c.RunKind));
    }
}
