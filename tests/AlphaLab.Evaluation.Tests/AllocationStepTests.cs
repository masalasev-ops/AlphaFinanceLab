using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation;
using AlphaLab.Evaluation.Allocator;
using AlphaLab.Evaluation.Monitor;

namespace AlphaLab.Evaluation.Tests;

/// <summary>End-to-end evaluation over the synthetic arena: gate → monitor → allocator, then the
/// allocation_log is the reconstructible D51 record.</summary>
public class AllocationStepTests
{
    [Fact]
    public void Run_AfterGateAndMonitor_PersistsReconstructibleAllocationLog()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(90, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(89, 0.008, seed: 5));
        arena.SeedStrategy("cand:a", "candidate", dates, EvalArena.Noise(89, 0.01, seed: 11));
        arena.SeedStrategy("cand:b", "candidate", dates, EvalArena.Noise(89, 0.01, seed: 12));
        var popId = arena.SeedPopulation("daily", true, 1001, dates, i => EvalArena.Noise(89, 0.01, 3000 + i), m: 30);

        using var db = arena.Open();
        var gate = new GateOptions();
        new EvaluationStep(db, gate).Run(dates[^1]);
        new OverfittingMonitor(db, gate).Run(dates[^1], "buyhold:cw", popId);
        var outcome = new AllocationStep(db, gate, new AllocatorOptions()).Run(dates[^1]);

        // One allocation_log row for the day; both candidates allocated; weights renormalized.
        var row = Assert.Single(db.AllocationLog.ToList());
        Assert.Equal("live", row.RunKind);
        Assert.Contains("cand:a", row.WeightsJson);
        Assert.Contains("cand:b", row.WeightsJson);
        Assert.Equal(2, outcome.Rows.Count);
        Assert.Equal(1.0, outcome.Rows.Sum(r => r.Weight), 9);

        // The benchmark never receives weight.
        Assert.DoesNotContain(outcome.Rows, r => r.StrategyId == "buyhold:cw");
    }

    [Fact]
    public void Run_ExcludesAStrategyRetiredThisEvaluation()
    {
        // The gate writes a power_reports row for a strategy while it is still a candidate; if the monitor
        // then auto-retires it in the same eval, the allocator must NOT still allocate it.
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(90, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("cand:live", "candidate", dates, EvalArena.Noise(89, 0.01, seed: 3));
        arena.SeedStrategy("cand:retired", "retired", dates, EvalArena.Noise(89, 0.01, seed: 4));

        using var db = arena.Open();
        foreach (var id in new[] { "cand:live", "cand:retired" })
        {
            db.PowerReports.Add(new PowerReportRow
            {
                AsOf = dates[^1], StrategyA = id, StrategyB = "buyhold:cw", TDays = 89, SigmaLr = 0.01,
                NwLag = 21, MdeAnn = 0.5, ObservedGapAnn = 0.05, Verdict = "TooEarly", RunKind = "live",
            });
        }
        db.SaveChanges();

        var outcome = new AllocationStep(db, new GateOptions(), new AllocatorOptions()).Run(dates[^1]);

        Assert.Contains(outcome.Rows, r => r.StrategyId == "cand:live");
        Assert.DoesNotContain(outcome.Rows, r => r.StrategyId == "cand:retired");   // retired ⇒ never allocated
    }

    [Fact]
    public void Run_SuspectStrategy_FromTheMonitor_GetsSuspectDecayInTheAllocation()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 30;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", true, 1001, dates, i => members[i], m);
        var centroid = Enumerable.Range(0, 99).Select(t => members.Average(mm => mm[t])).ToArray();

        arena.SeedStrategy("cand:anti", "candidate", dates, centroid.Select(r => r - 0.003).ToArray());   // below the 25th pct ⇒ Suspect
        arena.SeedStrategy("cand:ok", "candidate", dates, EvalArena.Noise(99, 0.01, seed: 99));

        using var db = arena.Open();
        // Change 3 (D63): a sub-anchor dip is Suspect only when SUSTAINED, so seed the prior below-anchor S3
        // checks a real run of Suspect evals would have left — this evaluation then reads Suspect and flows
        // its decay into the allocator clamp.
        foreach (var d in new[] { "2026-01-01", "2026-01-02", "2026-01-03" })
            db.OverfittingChecks.Add(new OverfittingCheckRow { StrategyId = "cand:anti", AsOf = d, Signal = "S3", Value = 5.0, ThresholdJson = "{}", Contribution = "suspect", RunKind = "live" });
        db.SaveChanges();

        var gate = new GateOptions();
        new EvaluationStep(db, gate).Run(dates[^1]);
        new OverfittingMonitor(db, gate).Run(dates[^1], "buyhold:cw", popId);
        var outcome = new AllocationStep(db, gate, new AllocatorOptions()).Run(dates[^1]);

        var anti = outcome.Rows.Single(r => r.StrategyId == "cand:anti");
        Assert.Contains("suspect_decay", anti.ClampsBound);   // the monitor's Suspect flows into the allocator clamp
        Assert.Equal(1.0, outcome.Rows.Sum(r => r.Weight), 9);
    }
}
