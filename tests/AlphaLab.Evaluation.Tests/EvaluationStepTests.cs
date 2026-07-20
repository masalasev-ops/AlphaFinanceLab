using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation;
using AlphaLab.Evaluation.Gate;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// A migrated temp-SQLite arena seeded with synthetic strategies + equity curves — the light "synthetic
/// arena" seam that exercises the evaluation step / gate without the full daily pipeline (D-E). Reused by
/// the gate/monitor/allocator checkpoints.
/// </summary>
internal sealed class EvalArena : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "alphalab-eval-" + Guid.NewGuid().ToString("N") + ".db");

    public EvalArena()
    {
        using var db = Open();
        db.Database.Migrate();
    }

    public AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    /// <summary>Seed a strategy + a live account + a daily equity curve built from
    /// <paramref name="dailyReturns"/> (length = <paramref name="dates"/>.Count − 1).</summary>
    public void SeedStrategy(string strategyId, string status, IReadOnlyList<string> dates,
        IReadOnlyList<double> dailyReturns, decimal startEquity = 100_000m, int? horizonDays = null)
    {
        using var db = Open();
        db.Strategies.Add(new StrategyRow
        {
            StrategyId = strategyId, Family = "test", ConfigJson = "{}", ExitPolicyJson = "{}",
            HoldingHorizonDays = horizonDays, CreatedOn = dates[0], Status = status,
        });
        var account = new AccountRow { StrategyId = strategyId, StartingCash = startEquity, RunKind = "live" };
        db.Accounts.Add(account);
        db.SaveChanges();

        var equity = startEquity;
        db.EquityCurve.Add(Row(account.AccountId, dates[0], equity));
        for (var i = 1; i < dates.Count; i++)
        {
            equity *= (decimal)(1.0 + dailyReturns[i - 1]);
            db.EquityCurve.Add(Row(account.AccountId, dates[i], equity));
        }
        db.SaveChanges();

        static EquityCurveRow Row(long acct, string asOf, decimal eq) =>
            new() { AccountId = acct, AsOf = asOf, Equity = eq, Cash = eq, RunKind = "live" };
    }

    /// <summary>Seed a control population: a control_populations row + M members' control_equity curves,
    /// each built from <paramref name="memberReturns"/>(i) (length = dates.Count − 1). Returns population_id.</summary>
    public long SeedPopulation(string family, bool costsOn, int seed, IReadOnlyList<string> dates,
        Func<int, IReadOnlyList<double>> memberReturns, int m, decimal startEquity = 100_000m)
    {
        using var db = Open();
        var pop = new ControlPopulationRow
        {
            Family = family, FamilySeed = seed, M = m, CostsOn = costsOn, MatchedParamsJson = "{}",
        };
        db.ControlPopulations.Add(pop);
        db.SaveChanges();

        for (var i = 0; i < m; i++)
        {
            var rets = memberReturns(i);
            var equity = startEquity;
            db.ControlEquity.Add(new ControlEquityRow { PopulationId = pop.PopulationId, MemberIndex = i, AsOf = dates[0], Equity = equity, RunKind = "live" });
            for (var t = 1; t < dates.Count; t++)
            {
                equity *= (decimal)(1.0 + rets[t - 1]);
                db.ControlEquity.Add(new ControlEquityRow { PopulationId = pop.PopulationId, MemberIndex = i, AsOf = dates[t], Equity = equity, RunKind = "live" });
            }
        }
        db.SaveChanges();
        return pop.PopulationId;
    }

    public static IReadOnlyList<string> Dates(int n, DateOnly start)
    {
        var dates = new List<string>(n);
        for (var i = 0; i < n; i++) dates.Add(start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return dates;
    }

    /// <summary>Deterministic Gaussian shocks (Box–Muller, fixed seed) scaled to a daily sigma.</summary>
    public static double[] Noise(int n, double sigma, int seed)
    {
        var rng = new Random(seed);
        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            x[i] = sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        return x;
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }
}

public class EvaluationStepTests
{
    [Fact]
    public void Run_ClearEdge_LongTrack_PromotesAgainstBenchmark_AndPersistsPowerReport()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());     // flat benchmark
        arena.SeedStrategy("cand:edge", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());   // +0.1%/day, zero variance

        using var db = arena.Open();
        var results = new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        var pr = Assert.Single(db.PowerReports.ToList());
        Assert.Equal("cand:edge", pr.StrategyA);
        Assert.Equal("buyhold:cw", pr.StrategyB);
        Assert.True(pr.ObservedGapAnn > 0);
        // A constant positive difference has zero variance ⇒ MDE 0 ⇒ decisively distinguishable.
        Assert.Equal("Promoted", pr.Verdict);
        Assert.Equal(PromotionVerdict.Promoted, results.Single().Verdict);
    }

    [Fact]
    public void Run_TinyEdge_NoisyPairing_IsTooEarly_InsideTheMde()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        var bench = EvalArena.Noise(99, 0.01, seed: 1);
        var candNoise = EvalArena.Noise(99, 0.005, seed: 2);
        // A tiny 0.02%/day edge (~5%/yr) buried under loose, independent pairing noise ⇒ gap ≪ MDE.
        var cand = bench.Select((b, i) => b + 0.0002 + candNoise[i]).ToArray();
        arena.SeedStrategy("buyhold:cw", "baseline", dates, bench);
        arena.SeedStrategy("cand:weak", "candidate", dates, cand);

        using var db = arena.Open();
        var result = new EvaluationStep(db, new GateOptions()).Run(dates[^1]).Single();

        Assert.Equal(PromotionVerdict.TooEarly, result.Verdict);
        Assert.True(Math.Abs(result.ObservedGapAnn) < result.MdeAnn);
    }

    [Fact]
    public void Run_ShortTrack_IsTooEarly_RegardlessOfGap()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));   // < MinTrackDays (63)
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 29).ToArray());
        arena.SeedStrategy("cand:edge", "candidate", dates, Enumerable.Repeat(0.002, 29).ToArray());   // huge edge

        using var db = arena.Open();
        var result = new EvaluationStep(db, new GateOptions()).Run(dates[^1]).Single();

        Assert.Equal(PromotionVerdict.TooEarly, result.Verdict);
    }

    [Fact]
    public void Run_NoBenchmarkAccount_ProducesNothing()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("cand:edge", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());   // no benchmark seeded

        using var db = arena.Open();
        var results = new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.Empty(results);
        Assert.Empty(db.PowerReports.ToList());
    }

    [Fact]
    public void FX_PairedWin_Promoted_TransitionsCandidateToLive_AndLogsTheEvent()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:edge", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.Equal("live", db.Strategies.Single(s => s.StrategyId == "cand:edge").Status);
        var ev = Assert.Single(db.GoLiveLog.ToList());
        Assert.Equal("cand:edge", ev.Promoted);
        Assert.Null(ev.Demoted);
        Assert.Equal("Promoted", ev.Verdict);
        Assert.Contains("observed_gap_ann", ev.EvidenceJson);
        Assert.Equal("live", ev.RunKind);
    }

    [Fact]
    public void Run_TooEarly_LeavesStatusCandidate_AndLogsNoGoLiveEvent()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));   // short track ⇒ TooEarly
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 29).ToArray());
        arena.SeedStrategy("cand:edge", "candidate", dates, Enumerable.Repeat(0.002, 29).ToArray());

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.Equal("candidate", db.Strategies.Single(s => s.StrategyId == "cand:edge").Status);
        Assert.Empty(db.GoLiveLog.ToList());
    }

    [Fact]
    public void Promotions_AcrossANoEdgePopulation_AreAtMostChance()
    {
        // The core acceptance property (§5.2 "gate sanity"): run the gate over a population of no-edge
        // candidates (independent noise vs a noisy benchmark). Because the MDE = 2.8·σ_LR·252/√T is exactly
        // the confidence/power threshold, the gate promotes only when the gap exceeds ~2.8 standard errors
        // — i.e. at the false-positive rate. Promotions must be ≤ chance.
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(120, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(119, 0.01, seed: 7));

        const int n = 40;
        for (var i = 0; i < n; i++)
            arena.SeedStrategy($"rand:{i}", "candidate", dates, EvalArena.Noise(119, 0.01, seed: 1000 + i));

        using var db = arena.Open();
        var results = new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        var promoted = results.Count(r => r.Verdict == PromotionVerdict.Promoted);
        Assert.True(promoted <= 2, $"{promoted}/{n} no-edge candidates promoted — must be ≤ chance");
    }

    [Fact]
    public void Run_ExcludesBaselinesAndOnlyScoresPromotableStrategies()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(80, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 79).ToArray());
        arena.SeedStrategy("buyhold:ew", "baseline", dates, Enumerable.Repeat(0.0005, 79).ToArray());  // a baseline — never scored
        arena.SeedStrategy("cand:a", "candidate", dates, Enumerable.Repeat(0.001, 79).ToArray());
        arena.SeedStrategy("live:b", "live", dates, Enumerable.Repeat(0.0008, 79).ToArray());

        using var db = arena.Open();
        var results = new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        var scored = results.Select(r => r.StrategyId).OrderBy(s => s).ToList();
        Assert.Equal(["cand:a", "live:b"], scored);   // both baselines excluded
    }
}
