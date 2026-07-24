using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Populations;
using AlphaLab.Evaluation.ReadModels;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// Checkpoint 4.7: the FX-Replay15y machinery-validation suite at CI scale (the SAME code the
/// full-scale run reports through — a check that cannot be evaluated at this scale reads
/// INSUFFICIENT, visibly, never a hollow green), the FR-41 per-regime decomposition, the FR-42
/// learn/validate no-leak invariant, and the §1.2 allocator value-add D58 read-model field.
/// </summary>
public class ReplayVerificationTests
{
    private static ReplayRunner Runner() =>
        new(ReplayEngineTests.HarnessConfiguration(),
            new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, NullLoggerFactory.Instance);

    // 31 sessions — enough for one 21-day evaluation cadence to fire inside the replay.
    private static ReplayRequest Window(PipelineHarness h) => new(h.Sessions[5], h.Sessions[35]);

    private static IReadOnlyList<PlantSpec> Specs() =>
        PlantCohorts.Build(new PlantOptions { SeedsPerPlant = 2 },
            PopulationFamilies.ForPhase3(new PopulationsOptions { Size = 6, CostFreeSize = 3 }));

    [Fact]
    public async Task FX_Replay15y_Mini_VerificationRuns_NoFailures()
    {
        using var h = new PipelineHarness();
        var outcome = await Runner().RunAsync($"Data Source={h.DbPath}", Window(h));
        Assert.False(outcome.StoppedEarly);

        using var db = h.Open();
        Assert.True(db.PowerReports.Any(p => p.RunKind == "replay"), "the 21-day cadence should have evaluated inside the window");

        var report = new ReplayVerification(db, new GateOptions(), new VerdictsOptions(), new ReplayOptions(), new PlantOptions())
            .Run(Specs(), learnThrough: h.Sessions[30], builtPNoise: null);

        // Every check computed; NOTHING failed. At this scale the statistical checks read
        // INSUFFICIENT — honest, visible, and exactly what the full-scale report upgrades to Pass.
        Assert.True(report.NoFailures,
            string.Join("\n", report.Checks.Where(c => c.Outcome == CheckOutcome.Fail).Select(c => $"{c.Name}: {c.Detail}")));
        Assert.Contains(report.Checks, c => c is { Name: "promotions_le_chance", Outcome: CheckOutcome.Pass });
        Assert.Contains(report.Checks, c => c is { Name: "would_be_edge_survival_5y", Outcome: CheckOutcome.Insufficient });
        Assert.Contains(report.Checks, c => c is { Name: "days_to_indistinguishability", Outcome: CheckOutcome.Insufficient });
        // Change 2: the curve-based checks read Insufficient with no built P_noise — honest, not a hollow green.
        Assert.Contains(report.Checks, c => c is { Name: "noedge_curve_breach_validate", Outcome: CheckOutcome.Insufficient });
        Assert.Contains(report.Checks, c => c is { Name: "curve_based_edge_survival", Outcome: CheckOutcome.Insufficient });
        Assert.Contains(report.Checks, c => c is { Name: "cohort_s3_paths_present", Outcome: CheckOutcome.Pass });
        Assert.False(report.AllGreen);   // Insufficient is NOT green — the full-scale bar stays honest

        // The KPI record exists and the value-add pair row was persisted, quarantined.
        Assert.NotNull(report.Kpis.ValueAdd);
        var pair = Assert.Single(db.PowerReports.Where(p => p.StrategyA == AllocatorValueAddKpi.BlendId).ToList());
        Assert.Equal("replay", pair.RunKind);
        Assert.Equal(AllocatorValueAddKpi.EqualWeightId, pair.StrategyB);
    }

    // Phase-4 review: the finding-113 survival floor judges the MIN-ALPHA D64 cohort (daily survival + monthly
    // base), NEVER pooled with the higher monthly ladder rungs (the C-1 detection-power sweep). Fixture:
    // SeedsPerPlant=5 -> floor cohort 10 (daily-2% ×5 + monthly-2% ×5), sweep 15 (monthly 4/8/16 ×5).
    // WOULD-retiring 2 floor plants (Change 1/2: survival reads the 'WouldRevert' log, not the
    // exemption-suppressed 'retired' status) makes the floor cohort 8/10 = 0.80 (FAIL at the 0.90 floor)
    // while the OLD pooled rate would read 23/25 = 0.92 and pass — masking exactly the S6-patience
    // recalibration the floor exists to fire.
    [Fact]
    public void FX_EdgeSurvivalFloor_JudgesTheD64Cohorts_SweepNeverDilutes()
    {
        using var h = new PipelineHarness();
        var specs = PlantCohorts.Build(new PlantOptions { SeedsPerPlant = 5 },
            PopulationFamilies.ForPhase3(new PopulationsOptions { Size = 6, CostFreeSize = 3 }));

        using var db = h.Open();
        // A synthetic 5-year generation: 1260 ok replay run rows (dates only matter ordinally).
        var start = new DateOnly(2010, 1, 1);
        for (var i = 0; i < 5 * 252; i++)
        {
            db.Runs.Add(new RunRow
            {
                AsOf = start.AddDays(i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                RunKind = "replay", Watermark = "2026-01-01T00:00:00Z",
                StartedAt = "2026-01-01T00:00:00Z", FinishedAt = "2026-01-01T00:00:01Z", Status = "ok",
            });
        }

        var floorDaily = specs.Where(s => s is { Kind: PlantKind.Edge, Family: "daily" })
            .GroupBy(s => s.AlphaAnnPct).OrderBy(g => g.Key).First().Select(s => s.StrategyId).Take(2).ToList();
        foreach (var id in floorDaily)
        {
            // The would-be retire log the calibration replay writes in lieu of an actual retire (Change 1):
            // a 'WouldRevert' row carrying its triggering signal — this is what survival now reads.
            db.GoLiveLog.Add(new GoLiveLogRow
            {
                AsOf = start.AddDays(400).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Demoted = id, Verdict = "WouldRevert",
                EvidenceJson = "{\"reason\":\"would_auto_retire\",\"trigger\":\"consecutive_suspect\"}", RunKind = "replay",
            });
        }
        db.SaveChanges();

        var report = new ReplayVerification(db, new GateOptions(), new VerdictsOptions(), new ReplayOptions(), new PlantOptions())
            .Run(specs, learnThrough: null, builtPNoise: null);

        var survival = Assert.Single(report.Checks, c => c.Name == "would_be_edge_survival_5y");
        Assert.Equal(CheckOutcome.Fail, survival.Outcome);       // 8/10 < 0.90 — the sweep cannot mask it
        Assert.Contains("sweep excluded", survival.Detail, StringComparison.Ordinal);
        Assert.Equal(0.80, report.Kpis.WouldBeEdgeSurvival5y!.Value, 10);
    }

    // Change 2 DoD: the curve-based metrics re-score each plant's HELD-OUT validate S3 path against the built
    // P_noise with the S3Trajectory SUSTAIN logic (per-plant, not point-level). A mid-band no-edge plant does
    // NOT sustain-breach; an edge plant above the noise floor survives; a plant that sustains BELOW the curve
    // is flagged either way — so both metrics respond, each gated by its OWN threshold key.
    [Fact]
    public void Change2_CurveBasedMetrics_ScoreSustainedValidateBreaches()
    {
        using var h = new PipelineHarness();
        var specs = PlantCohorts.Build(new PlantOptions { SeedsPerPlant = 3 },
            PopulationFamilies.ForPhase3(new PopulationsOptions { Size = 6, CostFreeSize = 3 }));
        var noEdge = specs.Where(s => s.Kind == PlantKind.NoEdge).Select(s => s.StrategyId).ToList();
        var edges = specs.Where(s => s.Kind == PlantKind.Edge).ToList();
        var minAlpha = edges.Min(s => s.AlphaAnnPct);
        var floorEdge = edges.Where(s => s.AlphaAnnPct == minAlpha).Select(s => s.StrategyId).ToList();

        using var db = h.Open();
        var boundary = h.Sessions[20];
        void Path(string id, params (int session, double pct)[] pts)
        {
            foreach (var (session, pct) in pts)
                db.OverfittingChecks.Add(new OverfittingCheckRow
                {
                    StrategyId = id, AsOf = h.Sessions[session], Signal = "S3", Value = pct,
                    ThresholdJson = "{}", Contribution = "in_band", RunKind = "replay",
                });
        }
        // Two learn points (≤ boundary) + two validate points (> boundary) per plant. No-edge plants sit
        // mid-band on validate EXCEPT the first, which sustains sub-noise; floor-edge plants stay above the
        // noise floor EXCEPT the first, which sustains sub-noise.
        foreach (var id in noEdge.Skip(1)) Path(id, (10, 50), (15, 50), (25, 50), (30, 50));
        Path(noEdge[0], (10, 50), (15, 50), (25, 8), (30, 8));
        foreach (var id in floorEdge.Skip(1)) Path(id, (10, 50), (15, 50), (25, 60), (30, 60));
        Path(floorEdge[0], (10, 50), (15, 50), (25, 8), (30, 8));
        db.SaveChanges();

        // A flat P_noise at the 20th percentile, sustain=2 ⇒ "breach" = < 20 for two consecutive validate evals.
        var pNoise = new S3Curve("p_noise", "daily", "piecewise_linear", 2, 0.05, [new CurveKnot(0, 20.0)], [], 0, null);

        var report = new ReplayVerification(db, new GateOptions(), new VerdictsOptions(), new ReplayOptions(), new PlantOptions())
            .Run(specs, learnThrough: boundary, builtPNoise: pNoise);

        // Exactly the one sustained-sub-noise no-edge plant breaches; exactly the one sustained-sub-noise
        // floor-edge plant fails to survive.
        Assert.Equal(1.0 / noEdge.Count, report.Kpis.NoEdgeCurveBreachValidate!.Value, 10);
        Assert.Equal((floorEdge.Count - 1) / (double)floorEdge.Count, report.Kpis.CurveBasedEdgeSurvival!.Value, 10);
        Assert.Contains(report.Checks, c => c.Name == "noedge_curve_breach_validate" && c.Outcome != CheckOutcome.Insufficient);
        Assert.Contains(report.Checks, c => c.Name == "curve_based_edge_survival" && c.Outcome != CheckOutcome.Insufficient);
    }

    // Change 4 (B3) DoD: PrimaryEdgeIds is RULE-selected — the smallest ladder rung clearing that cadence's
    // pre-registered offline floor. At the defaults (daily floor ~37% unreachable; monthly {2,4,8,16} vs
    // ~15.9%) that is the 16% MONTHLY rung. Daily is IN the survival floor cohort and OUT of the primary, so
    // a later refactor cannot silently re-include daily in edge_plant_detected and reintroduce the hard fail.
    [Fact]
    public void Change4_PrimaryEdgeIds_RuleSelectsSmallestClearingRung_MonthlyNotDaily()
    {
        var plant = new PlantOptions();
        var specs = PlantCohorts.Build(plant, PopulationFamilies.ForPhase3(new PopulationsOptions { Size = 6, CostFreeSize = 3 }));

        var primary = ReplayVerification.PrimaryEdgeIds(specs, plant);
        var floor = ReplayVerification.FloorEdgeIds(specs);

        // The primary is every monthly @ 16% plant, and NOTHING daily.
        Assert.NotEmpty(primary);
        Assert.All(primary, id => Assert.StartsWith("plant:edge:monthly:16:", id, StringComparison.Ordinal));
        Assert.DoesNotContain(primary, id => id.StartsWith("plant:edge:daily", StringComparison.Ordinal));

        // Daily survival plants are IN the floor cohort but OUT of the primary; the floor is the min-alpha
        // cohort (daily 2 + monthly 2), never the higher sweep rungs.
        Assert.Contains(floor, id => id.StartsWith("plant:edge:daily:2:", StringComparison.Ordinal));
        Assert.Contains(floor, id => id.StartsWith("plant:edge:monthly:2:", StringComparison.Ordinal));
        Assert.DoesNotContain(floor, id => id.StartsWith("plant:edge:monthly:16", StringComparison.Ordinal));

        // Raising the monthly floor above the top rung ⇒ no cadence clears ⇒ empty primary (a recorded finding),
        // never a silent fallback to daily.
        Assert.Empty(ReplayVerification.PrimaryEdgeIds(specs, new PlantOptions { MonthlyMdeFloorPct = 99.0 }));
    }

    [Fact]
    public async Task FX_ReplayPerRegime_RowsPartitionTheWindow_AllQuarantined()
    {
        using var h = new PipelineHarness();
        await Runner().RunAsync($"Data Source={h.DbPath}", Window(h));

        using var db = h.Open();
        // The mini window sits below the regime warm-up (no computed labels), so seed a synthetic
        // REPLAY episode chain that partitions it — the writer's input shape, without 3.8y of proxy.
        db.RegimeEpisodes.Add(new RegimeEpisodeRow { Label = "bull", StartDate = h.Sessions[5], EndDate = h.Sessions[20], RunKind = "replay" });
        db.RegimeEpisodes.Add(new RegimeEpisodeRow { Label = "bear", StartDate = h.Sessions[21], EndDate = null, RunKind = "replay" });
        db.SaveChanges();

        var written = new ReplayRegimeOutcomesWriter(db).Write("buyhold:cw");
        Assert.True(written > 0);

        var rows = db.ReplayRegimeOutcomes.ToList();
        Assert.All(rows, r => Assert.Equal("replay", r.RunKind));

        // Aggregation identity: episodes partition the window's dates, so Σ n_days over one strategy's
        // rows equals its replay curve length (FX-ReplayPerRegime "rows aggregate to the overall outcome").
        var ewAccount = db.Accounts.Single(a => a.StrategyId == "buyhold:ew" && a.RunKind == "replay").AccountId;
        var curveDays = db.EquityCurve.Count(e => e.AccountId == ewAccount && e.RunKind == "replay");
        var ewRows = rows.Where(r => r.StrategyId == "buyhold:ew").ToList();
        Assert.Equal(curveDays, ewRows.Sum(r => r.NDays));

        // Idempotent: a re-run rewrites, never duplicates (the composite PK would throw otherwise).
        new ReplayRegimeOutcomesWriter(db).Write("buyhold:cw");
        Assert.Equal(rows.Count, db.ReplayRegimeOutcomes.Count());
    }

    [Fact]
    public void FX_ReplayPartition_NoLeak_ValidateDataCannotMoveTheCurves()
    {
        using var h = new PipelineHarness();
        using var db = h.Open();
        // Hand-crafted S3 percentile paths: learn rows ≤ B, validate rows after B.
        var boundary = h.Sessions[20];
        void AddS3(string strategyId, string asOf, double pct) =>
            db.OverfittingChecks.Add(new OverfittingCheckRow
            {
                StrategyId = strategyId, AsOf = asOf, Signal = "S3", Value = pct,
                ThresholdJson = "{}", Contribution = "in_band", RunKind = "replay",
            });
        AddS3("p1", h.Sessions[10], 40); AddS3("p1", h.Sessions[15], 45); AddS3("p1", h.Sessions[25], 90);
        AddS3("p2", h.Sessions[10], 50); AddS3("p2", h.Sessions[15], 55); AddS3("p2", h.Sessions[25], 95);
        db.SaveChanges();

        S3Curve Build()
        {
            var paths = CurveBuilder.PercentilePaths(db, ["p1", "p2"], boundary);
            return CurveBuilder.BuildEdge(paths.Values.ToList(), "daily", 21, 2, 0.05, 200, null);
        }
        var before = Build().ToJson();

        // Mutate the VALIDATE period only: new rows + extreme values after the boundary. The FR-42
        // leakage invariant (extending F-LEAK): no validate-period datum reaches a learn computation.
        AddS3("p1", h.Sessions[30], 1.0);
        AddS3("p2", h.Sessions[30], 99.0);
        db.SaveChanges();

        Assert.Equal(before, Build().ToJson());
    }

    [Fact]
    public async Task FR33_AllocationReadModel_ValueAdd_QuarantineMarked()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);   // a forward run, so the stamp resolves

        // Before any replay: the field is absent — nothing fabricated.
        using (var db = h.Open())
        {
            Assert.Null(new AllocationReadModelBuilder(db).Build().AllocatorValueAdd);
        }

        await Runner().RunAsync($"Data Source={h.DbPath}", Window(h));
        using (var db = h.Open())
        {
            new ReplayVerification(db, new GateOptions(), new VerdictsOptions(), new ReplayOptions(), new PlantOptions())
                .Run(Specs(), null, null);   // persists the pair row
            var valueAdd = new AllocationReadModelBuilder(db).Build().AllocatorValueAdd;
            Assert.NotNull(valueAdd);
            Assert.True(valueAdd!.Quarantined);            // the honesty is IN the DTO (D58)
            Assert.Equal("replay", valueAdd.Source);
        }
    }

    [Fact]
    public async Task Replay_ReadModel_SummarizesTheGeneration_AlwaysQuarantined()
    {
        using var h = new PipelineHarness();
        using (var db = h.Open())
        {
            Assert.True(new ReplayReadModelBuilder(db).Build().Quarantined);   // NoRunYet is still flagged
        }

        await h.RunAsync(h.Run1);
        await Runner().RunAsync($"Data Source={h.DbPath}", Window(h));
        using (var db2 = h.Open())
        {
            var model = new ReplayReadModelBuilder(db2).Build();
            Assert.True(model.Quarantined);
            Assert.Single(model.Rows);

            // Phase-4 review (rule 20/D60): the stamp is the REPLAY generation's own provenance —
            // its latest run_id + the frozen watermark — never the forward run's identity.
            var lastReplay = db2.Runs.Where(r => r.RunKind == "replay" && r.Status == "ok")
                .OrderByDescending(r => r.AsOf).First();
            Assert.Equal(lastReplay.RunId, model.Stamp.RunId);
            Assert.Equal(lastReplay.Watermark, model.Stamp.Watermark);
            var forwardRun = db2.Runs.Single(r => r.AsOf == h.Run1 && r.RunKind == "live");
            Assert.NotEqual(forwardRun.RunId, model.Stamp.RunId);
        }
    }

    // Phase-4 review: a replay-only store (the rebuild path) has a committed generation and NO forward
    // run — the screen must carry the generation's stamp, never a "no run yet" shell around real rows.
    [Fact]
    public async Task Replay_ReadModel_ReplayOnlyStore_IsStampedWithTheGeneration()
    {
        using var h = new PipelineHarness();
        await Runner().RunAsync($"Data Source={h.DbPath}", Window(h));

        using var db = h.Open();
        var model = new ReplayReadModelBuilder(db).Build();
        Assert.Single(model.Rows);
        Assert.NotEqual(AlphaLab.Core.ReadModels.ReadModelStampStatus.NoRunYet, model.Stamp.Status);
        Assert.NotNull(model.Stamp.Watermark);
    }
}
