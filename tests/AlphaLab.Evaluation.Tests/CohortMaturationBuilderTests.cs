using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.ReadModels;
using DataStrategy = AlphaLab.Data.Entities.StrategyRow;

namespace AlphaLab.Evaluation.Tests;

public class CohortMaturationBuilderTests
{
    private static void SeedStrategy(AlphaLabDbContext db, string id, string status, string createdOn) =>
        db.Strategies.Add(new DataStrategy
        {
            StrategyId = id, Family = "momentum", ConfigJson = "{}", ExitPolicyJson = "{}",
            HoldingHorizonDays = 21, CreatedOn = createdOn, Status = status,
        });

    private static void SeedS3(AlphaLabDbContext db, string id, string runKind, params double[] percentiles)
    {
        var day = new DateOnly(2026, 3, 1);
        foreach (var p in percentiles)
        {
            db.OverfittingChecks.Add(new OverfittingCheckRow
            {
                StrategyId = id, AsOf = day.ToString("yyyy-MM-dd"), Signal = "S3",
                Value = p, ThresholdJson = "{\"n\":200}", Contribution = "in_band", RunKind = runKind,
            });
            day = day.AddDays(21);
        }
    }

    [Fact]
    public void FX_CohortCurve_AgeAligned_RetiredRetained_ReplayQuarantined_ThinAndSubMdeDimmed()
    {
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            // H1 2026 (n=3; a3 RETIRED — retained, no survivorship): band ±8 around a median of 50.
            SeedStrategy(db, "a1", "candidate", "2026-02-01");
            SeedStrategy(db, "a2", "candidate", "2026-02-10");
            SeedStrategy(db, "a3", "retired", "2026-02-15");
            SeedS3(db, "a1", "live", 34); SeedS3(db, "a2", "live", 50); SeedS3(db, "a3", "live", 66);

            // H2 2026 (n=3): median 56 — a +6 gain vs H1 at equal t, inside the ±8 band ⇒ inside_mde.
            SeedStrategy(db, "b1", "candidate", "2026-08-01");
            SeedStrategy(db, "b2", "candidate", "2026-08-05");
            SeedStrategy(db, "b3", "candidate", "2026-08-10");
            SeedS3(db, "b1", "live", 40); SeedS3(db, "b2", "live", 56); SeedS3(db, "b3", "live", 72);

            // H1 2027 (n=2 < 3): thin cohort ⇒ thin_cohort.
            SeedStrategy(db, "c1", "candidate", "2027-02-01");
            SeedStrategy(db, "c2", "candidate", "2027-02-05");
            SeedS3(db, "c1", "live", 48); SeedS3(db, "c2", "live", 52);

            // A replay S3 path on an H1 2026 member ⇒ a quarantined replay cohort.
            SeedS3(db, "a1", "replay", 90);

            db.Runs.Add(new RunRow { AsOf = "2026-03-01", RunKind = "live", Watermark = "w", StartedAt = "t", Status = "ok" });
            db.SaveChanges();
        }

        using var read = arena.Open();
        var model = new CohortMaturationBuilder(read, new KpiOptions(), new GateOptions()).Build();

        var forward = model.Cohorts.Where(c => !c.Quarantined).ToList();
        var replay = model.Cohorts.Where(c => c.Quarantined).ToList();

        // (c) the replay cohort is quarantined and separate (never co-plotted).
        Assert.NotEmpty(replay);
        Assert.All(replay, c => Assert.True(c.Quarantined));

        // (a) age-aligned: the first evaluation is at t = 21 trading days for every cohort.
        var h1 = forward.Single(c => c.Label == "H1 2026");
        Assert.Equal(21, h1.Series[0].T);
        // (b) retired retained: H1 2026 has 3 members contributing at t.
        Assert.Equal(3, h1.MemberCount);
        Assert.Equal(3, h1.Series[0].MemberCountAtT);
        Assert.Equal(50.0, h1.Series[0].MedianPercentile, 6);

        // (e) the sub-MDE gain is dimmed, never claimed as an improvement.
        var h2 = forward.Single(c => c.Label == "H2 2026");
        Assert.Equal("dimmed", h2.Series[0].Display);
        Assert.Equal("inside_mde", h2.Series[0].Reason);

        // (d) the 2-member cohort is dimmed as thin.
        var thin = forward.Single(c => c.Label == "H1 2027");
        Assert.Equal("dimmed", thin.Series[0].Display);
        Assert.Equal("thin_cohort", thin.Series[0].Reason);
    }

    [Fact]
    public void UX15_CohortCurve_ThinAndSubMdeDimmed_ReplayNeverCoplotted()
    {
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            SeedStrategy(db, "f1", "candidate", "2026-02-01");
            SeedS3(db, "f1", "live", 60);
            SeedS3(db, "f1", "replay", 95);
            db.Runs.Add(new RunRow { AsOf = "2026-03-01", RunKind = "live", Watermark = "w", StartedAt = "t", Status = "ok" });
            db.SaveChanges();
        }

        using var read = arena.Open();
        var cohorts = new CohortMaturationBuilder(read, new KpiOptions(), new GateOptions()).Build().Cohorts;

        // Forward and replay never share a plotted series: they are distinct cohort objects, the replay one
        // flagged quarantined. A UI keys off the flag to render them in separate strips.
        var forward = cohorts.Single(c => !c.Quarantined);
        var replay = cohorts.Single(c => c.Quarantined);
        Assert.Equal(60.0, forward.Series[0].MedianPercentile, 6);   // forward reads the live path
        Assert.Equal(95.0, replay.Series[0].MedianPercentile, 6);    // replay reads the replay path
        Assert.NotSame(forward, replay);
    }

    [Fact]
    public void ReplayCohort_MemberCount_CountsOnlyMembersWithAPath_NotTheWholeVintage()
    {
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            SeedStrategy(db, "a1", "candidate", "2026-02-01");
            SeedStrategy(db, "a2", "candidate", "2026-02-10");
            SeedStrategy(db, "a3", "candidate", "2026-02-15");
            SeedS3(db, "a1", "live", 50); SeedS3(db, "a2", "live", 50); SeedS3(db, "a3", "live", 50);
            SeedS3(db, "a1", "replay", 90);   // ONLY a1 has a replay path
            db.Runs.Add(new RunRow { AsOf = "2026-03-01", RunKind = "live", Watermark = "w", StartedAt = "t", Status = "ok" });
            db.SaveChanges();
        }

        using var read = arena.Open();
        var cohorts = new CohortMaturationBuilder(read, new KpiOptions(), new GateOptions()).Build().Cohorts;

        Assert.Equal(1, cohorts.Single(c => c.Quarantined).MemberCount);    // replay reflects only a1, not 3
        Assert.Equal(3, cohorts.Single(c => !c.Quarantined).MemberCount);   // all three contribute a live path
    }

    [Fact]
    public void UndefinedEarlyS3_KeepsItsEvaluationSlot_SoTheFirstRealPointIsAgeAligned()
    {
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            SeedStrategy(db, "x", "candidate", "2026-02-01");
            SeedStrategy(db, "y", "candidate", "2026-02-01");   // a second member so the cohort is not thin at that point
            // Evals 0 + 1: S3 undefined (no matched population yet ⇒ Value null); eval 2: the first real percentile.
            foreach (var id in new[] { "x", "y" })
            {
                var day = new DateOnly(2026, 3, 1);
                foreach (var v in new double?[] { null, null, 60.0 })
                {
                    db.OverfittingChecks.Add(new OverfittingCheckRow
                    {
                        StrategyId = id, AsOf = day.ToString("yyyy-MM-dd"), Signal = "S3", Value = v,
                        ThresholdJson = "{\"n\":200}", Contribution = v is null ? "undefined" : "in_band", RunKind = "live",
                    });
                    day = day.AddDays(21);
                }
            }
            db.Runs.Add(new RunRow { AsOf = "2026-03-01", RunKind = "live", Watermark = "w", StartedAt = "t", Status = "ok" });
            db.SaveChanges();
        }

        using var read = arena.Open();
        var cohort = new CohortMaturationBuilder(read, new KpiOptions(), new GateOptions()).Build().Cohorts.Single(c => !c.Quarantined);

        // The lone real percentile sits at evaluation index 2 ⇒ T = 3×cadence (63), NOT collapsed to 21 by
        // dropping the two undefined-S3 rows before indexing.
        var pt = Assert.Single(cohort.Series);
        Assert.Equal(63, pt.T);
        Assert.Equal(60.0, pt.MedianPercentile, 6);
    }

    [Fact]
    public void Build_BeforeAnyCommittedRun_IsNoRunYet()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var model = new CohortMaturationBuilder(db, new KpiOptions(), new GateOptions()).Build();
        Assert.Equal(ReadModelStampStatus.NoRunYet, model.Stamp.Status);
    }
}
