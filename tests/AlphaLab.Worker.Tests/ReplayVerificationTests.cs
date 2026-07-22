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

        var report = new ReplayVerification(db, new GateOptions(), new VerdictsOptions(), new ReplayOptions())
            .Run(Specs(), learnThrough: h.Sessions[30], builtPNoise: null);

        // Every check computed; NOTHING failed. At this scale the statistical checks read
        // INSUFFICIENT — honest, visible, and exactly what the full-scale report upgrades to Pass.
        Assert.True(report.NoFailures,
            string.Join("\n", report.Checks.Where(c => c.Outcome == CheckOutcome.Fail).Select(c => $"{c.Name}: {c.Detail}")));
        Assert.Contains(report.Checks, c => c is { Name: "promotions_le_chance", Outcome: CheckOutcome.Pass });
        Assert.Contains(report.Checks, c => c is { Name: "edge_survival_5y", Outcome: CheckOutcome.Insufficient });
        Assert.Contains(report.Checks, c => c is { Name: "days_to_indistinguishability", Outcome: CheckOutcome.Insufficient });
        Assert.Contains(report.Checks, c => c is { Name: "cohort_s3_paths_present", Outcome: CheckOutcome.Pass });
        Assert.False(report.AllGreen);   // Insufficient is NOT green — the full-scale bar stays honest

        // The KPI record exists and the value-add pair row was persisted, quarantined.
        Assert.NotNull(report.Kpis.ValueAdd);
        var pair = Assert.Single(db.PowerReports.Where(p => p.StrategyA == AllocatorValueAddKpi.BlendId).ToList());
        Assert.Equal("replay", pair.RunKind);
        Assert.Equal(AllocatorValueAddKpi.EqualWeightId, pair.StrategyB);
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
            new ReplayVerification(db, new GateOptions(), new VerdictsOptions(), new ReplayOptions())
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
        }
    }
}
