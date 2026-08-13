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
        IReadOnlyList<double> dailyReturns, decimal startEquity = 100_000m, int? horizonDays = null,
        string runKind = "live")
    {
        using var db = Open();
        db.Strategies.Add(new StrategyRow
        {
            StrategyId = strategyId, Family = "test", ConfigJson = "{}", ExitPolicyJson = "{}",
            HoldingHorizonDays = horizonDays, CreatedOn = dates[0], Status = status,
        });
        var account = new AccountRow { StrategyId = strategyId, StartingCash = startEquity, RunKind = runKind };
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

        EquityCurveRow Row(long acct, string asOf, decimal eq) =>
            new() { AccountId = acct, AsOf = asOf, Equity = eq, Cash = eq, RunKind = runKind };
    }

    /// <summary>Seed a control population: a control_populations row + M members' control_equity curves,
    /// each built from <paramref name="memberReturns"/>(i) (length = dates.Count − 1). Returns population_id.</summary>
    public long SeedPopulation(string family, bool costsOn, int seed, IReadOnlyList<string> dates,
        Func<int, IReadOnlyList<double>> memberReturns, int m, decimal startEquity = 100_000m,
        string runKind = "live")
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
            db.ControlEquity.Add(new ControlEquityRow { PopulationId = pop.PopulationId, MemberIndex = i, AsOf = dates[0], Equity = equity, RunKind = runKind });
            for (var t = 1; t < dates.Count; t++)
            {
                equity *= (decimal)(1.0 + rets[t - 1]);
                db.ControlEquity.Add(new ControlEquityRow { PopulationId = pop.PopulationId, MemberIndex = i, AsOf = dates[t], Equity = equity, RunKind = runKind });
            }
        }
        db.SaveChanges();
        return pop.PopulationId;
    }

    /// <summary>A session's run watermark in the pipeline's own shape (`{asOf}T22:00:00Z` — D92/D95, and
    /// the instant `DailyPipeline` threads to the monitor). D141 made that watermark REQUIRED on
    /// <c>OverfittingMonitor.Run</c>, so a fixture must now state the instant it is evaluating at instead
    /// of falling into a run-time-current config read.</summary>
    public static string Watermark(string asOf) => $"{asOf}T22:00:00Z";

    public static IReadOnlyList<string> Dates(int n, DateOnly start)
    {
        var dates = new List<string>(n);
        for (var i = 0; i < n; i++) dates.Add(start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return dates;
    }

    /// <summary>Seed a committed 'ok' forward run so a read-model builder stamps rather than returning
    /// no_run_yet.</summary>
    public void SeedRun(string asOf, string runKind = "live")
    {
        using var db = Open();
        db.Runs.Add(new RunRow { AsOf = asOf, RunKind = runKind, Watermark = asOf + "T22:00:00Z", StartedAt = asOf, Status = "ok" });
        db.SaveChanges();
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
    public void Run_ShortHorizonStrategy_TakesTheBenchmarkHorizonForTheNwLag()
    {
        // A short-horizon strategy must still get the full-lag autocorrelation correction: the NW lag is
        // min(2·max(h_strat, h_benchmark), cap). The benchmark's null horizon ⇒ default 21, so the lag is 21
        // regardless of the strategy's own 5 — never min(2·5, 21) = 10, which would under-claim the MDE.
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.01, seed: 1));   // null horizon ⇒ 21
        arena.SeedStrategy("cand:fast", "candidate", dates, EvalArena.Noise(99, 0.01, seed: 2), horizonDays: 5);

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        var pr = db.PowerReports.Single(p => p.StrategyA == "cand:fast");
        Assert.Equal(21, pr.NwLag);
    }

    [Fact]
    public void FX_PairedWindowIsTheOverlap_LateForkIsJudgedOnItsOwnSessionsAtTheSameAlphaRate()
    {
        // Finding 372 — the mid-life fork. A candidate registered part-way through the arena's life has no
        // returns for the sessions before it existed. The pair is the COMMON-DATE INTERSECTION
        // (`CurveMath.AlignedReturns`), so the fork is judged on its own sessions and the incumbent's earlier
        // ones never enter its difference series: it is neither credited nor charged for them.
        //
        // The second assertion is the substantive one. Alpha is a RATE, not a level. Both strategies earn the
        // same +0.1%/day over the benchmark, so their annualized gaps agree exactly even though the incumbent
        // has 2.5x the track and a very different cumulative return. A gate that compared equity levels — the
        // intuitive design, and the wrong one — would rank a young fork below an incumbent for no reason but
        // its birthday.
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        var bench = EvalArena.Noise(99, 0.008, seed: 7);
        arena.SeedStrategy("buyhold:cw", "baseline", dates, bench);

        // One idiosyncratic series, read at the SAME index by both strategies, so on every overlapping
        // session the fork and the incumbent hold identical returns. Window length is then the only variable
        // between their two power reports. (It must be non-zero: a perfect β = 1 fit has no residual, hence
        // no standard error and a zero MDE for both — which would make assertion 3 vacuous rather than true.)
        var idio = EvalArena.Noise(99, 0.00005, seed: 8);

        // Incumbent: all 100 sessions, β = 1, +0.1%/day of alpha.
        arena.SeedStrategy("live:old", "live", dates, bench.Select((b, i) => b + 0.001 + idio[i]).ToArray());

        // Fork: born at session 60, the SAME construction over its own sub-window.
        const int birth = 60;
        var forkDates = dates.Skip(birth).ToList();
        arena.SeedStrategy("cand:fork", "candidate", forkDates,
            bench.Skip(birth).Select((b, j) => b + 0.001 + idio[birth + j]).ToArray());

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        var incumbent = db.PowerReports.Single(p => p.StrategyA == "live:old");
        var fork = db.PowerReports.Single(p => p.StrategyA == "cand:fork");

        // 1. The window is the overlap — the fork's own sessions, not the benchmark's full history.
        Assert.Equal(dates.Count - 1, incumbent.TDays);
        Assert.Equal(forkDates.Count - 1, fork.TDays);

        // 2. The same alpha RATE — both recover the planted 25.2%/yr — despite the 2.5x track difference.
        Assert.Equal(0.252, incumbent.ObservedGapAnn!.Value, 2);
        Assert.Equal(0.252, fork.ObservedGapAnn!.Value, 2);

        // 3. What the short track actually costs is POWER, not alpha: a wider MDE, and therefore the honest
        //    TooEarly rather than an adverse verdict. "Starts from zero" is zero TRACK, never zero standing.
        Assert.True(fork.MdeAnn > incumbent.MdeAnn);
        Assert.Equal("TooEarly", fork.Verdict);
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
    // ---------------------------------------------------------------------------------------------
    // D156 (6.5 PR 2, item a): THIS evaluation's monitor status governs THIS evaluation's gate.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// OVERFITTING_MONITOR §3, verbatim: "Suspect ⇒ promotion vetoed regardless of P&amp;L". The strategy
    /// here is WINNING — its gap clears the MDE and the gate writes `Promoted` into `power_reports` —
    /// and it is still not promoted, which is the whole content of "regardless of P&amp;L".
    ///
    /// <para>The `power_reports` verdict is deliberately still `Promoted`. Rewriting it would erase the
    /// evidence that a vetoed strategy was beating the benchmark, which is the only thing that makes the
    /// veto worth having; what the veto changes is whether the PROMOTION happens.</para>
    /// </summary>
    [Fact]
    public void D156_ASuspectStrategyIsNotPromoted_EvenWhenItsGapClearsTheMde()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();

        // The monitor's verdict for THIS evaluation, written before the gate runs — exactly the order
        // DailyPipeline now uses. Seeded directly so the test pins the GATE's behaviour rather than
        // re-deriving the monitor's, which has its own fixtures.
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = dates[^1], StrategyId = "cand:win", Status = "suspect", RunKind = "live", TriggerJson = "{}",
        });
        db.SaveChanges();

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        // The gate's ARITHMETIC still cleared the bar, and the record says so.
        var report = db.PowerReports.Single(p => p.StrategyA == "cand:win");
        Assert.Equal("Promoted", report.Verdict);

        // But the promotion did not happen: no go_live_log event, and the status never flipped.
        Assert.DoesNotContain(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
        Assert.Equal("candidate", db.Strategies.Single(s => s.StrategyId == "cand:win").Status);
    }

    /// <summary>The control, and the reason the test above is not vacuous: the identical strategy with a
    /// HEALTHY status on the same evaluation IS promoted. Without this, a bug that refused every promotion
    /// would pass the veto test.</summary>
    [Fact]
    public void D156_TheSameStrategyWithAHealthyStatus_IsPromoted()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = dates[^1], StrategyId = "cand:win", Status = "healthy", RunKind = "live", TriggerJson = "{}",
        });
        db.SaveChanges();

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.Equal("Promoted", db.PowerReports.Single(p => p.StrategyA == "cand:win").Verdict);
        Assert.Contains(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
        Assert.Equal("live", db.Strategies.Single(s => s.StrategyId == "cand:win").Status);
    }

    /// <summary>
    /// A strategy the monitor auto-retired in THIS evaluation is not promoted either — otherwise the gate
    /// would resurrect a strategy the arena had just killed, in the same transaction. Before the reorder
    /// this could not be expressed at the gate and was patched on the monitor's side instead, by writing
    /// an offsetting demotion row after the fact (`OverfittingMonitor`'s same-eval note).
    /// </summary>
    [Fact]
    public void D156_AStrategyRetiredThisEvaluation_IsNotPromotedBackToLife()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = dates[^1], StrategyId = "cand:win", Status = "retired", RunKind = "live", TriggerJson = "{}",
        });
        db.SaveChanges();

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.DoesNotContain(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
        Assert.Equal("candidate", db.Strategies.Single(s => s.StrategyId == "cand:win").Status);
    }

    /// <summary>
    /// The veto is SAME-EVALUATION, not "has ever been suspect". A strategy suspect on a PRIOR evaluation
    /// but healthy on this one is promotable — the monitor's own recovery path would be meaningless
    /// otherwise, and D156's whole subject is that the gate reads THIS evaluation's row.
    /// </summary>
    [Fact]
    public void D156_TheVetoReadsThisEvaluationOnly_NotAnyPriorSuspectRow()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = dates[^40], StrategyId = "cand:win", Status = "suspect", RunKind = "live", TriggerJson = "{}",
        });
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = dates[^1], StrategyId = "cand:win", Status = "healthy", RunKind = "live", TriggerJson = "{}",
        });
        db.SaveChanges();

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.Contains(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
    }
    // ---------------------------------------------------------------------------------------------
    // D157 (6.5 PR 2, item b): a Warning promotes only with a logged operator acknowledgment.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Seed a locked operator acknowledgment for one strategy on one evaluation, recording WHAT
    /// was acknowledged rather than merely that acknowledgment occurred.</summary>
    private static void SeedAck(AlphaLabDbContext db, string strategyId, string asOf, string what)
    {
        db.JournalEntries.Add(new JournalEntryRow
        {
            CreatedOn = asOf,
            Kind = EvaluationStep.WarningAckKind,
            Title = $"Warning acknowledged ({asOf}, {strategyId})",
            BodyMd = what,
            StrategyId = strategyId,
            Locked = true,
        });
        db.SaveChanges();
    }

    private static void SeedWarning(AlphaLabDbContext db, string strategyId, string asOf)
    {
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = asOf, StrategyId = strategyId, Status = "warning", RunKind = "live", TriggerJson = "{}",
        });
        db.SaveChanges();
    }

    /// <summary>
    /// THE REFUSAL, PROVEN REACHABLE. OVERFITTING_MONITOR §3: a Warning permits promotion "only with
    /// explicit operator acknowledgment (logged)". With no acknowledgment the winning strategy is refused
    /// — and this fixture replaces `D156_AWarningStillPromotes_TheAcknowledgmentRailIsNotBuiltYet`, which
    /// pinned the opposite on purpose so this PR would have a red to turn green.
    /// </summary>
    [Fact]
    public void D157_AWarningWithNoAcknowledgment_IsRefusedPromotion()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        SeedWarning(db, "cand:win", dates[^1]);

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        // The gate's arithmetic cleared the bar and the record still says so.
        Assert.Equal("Promoted", db.PowerReports.Single(p => p.StrategyA == "cand:win").Verdict);
        // The promotion did not happen.
        Assert.DoesNotContain(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
        Assert.Equal("candidate", db.Strategies.Single(s => s.StrategyId == "cand:win").Status);
    }

    /// <summary>The other arm: the SAME strategy, the same Warning, with a locked acknowledgment for that
    /// evaluation — promotes. Without this the refusal above would be indistinguishable from a gate that
    /// simply blocks every Warning, which is the option §3 deliberately does not take.</summary>
    [Fact]
    public void D157_TheSameWarningWithALockedAcknowledgment_Promotes()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        SeedWarning(db, "cand:win", dates[^1]);
        SeedAck(db, "cand:win", dates[^1], "S6 elevated_neg_alpha, rolling alpha t = -2.4; reviewed and accepted.");

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.Contains(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
        Assert.Equal("live", db.Strategies.Single(s => s.StrategyId == "cand:win").Status);
    }

    /// <summary>
    /// THE FORGERY GUARD. The gate reads only LOCKED rows, and `ResearchJobExecutor` can write journal
    /// rows only with <c>Locked = false</c> (rule 30: the AI proposes, only the operator pre-registers).
    /// So a seat cannot manufacture its own acknowledgment BY CONSTRUCTION — it has no code path that
    /// produces a locked row — rather than by a convention someone must remember.
    ///
    /// <para>This is what keeps the journal becoming a gate input from breaching the two-loops wall:
    /// without it, "the gate reads journal_entries" would mean "anything that can write a journal row can
    /// promote a strategy", and the researcher seat can write journal rows.</para>
    /// </summary>
    [Fact]
    public void D157_AnUnlockedAck_DoesNotSatisfyTheGate()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        SeedWarning(db, "cand:win", dates[^1]);
        db.JournalEntries.Add(new JournalEntryRow
        {
            CreatedOn = dates[^1],
            Kind = EvaluationStep.WarningAckKind,
            Title = "unlocked - the shape a seat could write",
            BodyMd = "S6 elevated",
            StrategyId = "cand:win",
            Locked = false,       // <- the only difference from the promoting fixture above
        });
        db.SaveChanges();

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.DoesNotContain(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
    }

    /// <summary>
    /// THE ACK BINDS TO THE EVALUATION, NOT THE STRATEGY — the difference between a control and a
    /// signature. An operator who acknowledged an S2 Warning on an earlier evaluation has not seen the
    /// S6 Warning firing on this one, and a strategy-bound acknowledgment would silently cover it.
    /// </summary>
    [Fact]
    public void D157_AnAckFromAnEarlierEvaluation_DoesNotCoverThisOne()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        SeedWarning(db, "cand:win", dates[^1]);
        SeedAck(db, "cand:win", dates[^40], "S2 deflated Sharpe elevated; reviewed on an earlier evaluation.");

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.DoesNotContain(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
    }

    /// <summary>An acknowledgment does not launder a SUSPECT. §3 separates the two deliberately: a
    /// Warning is acknowledgeable, a Suspect is "vetoed regardless of P&amp;L" and cannot be signed away.
    /// Without this, (b) would quietly weaken (a).</summary>
    [Fact]
    public void D157_AnAcknowledgmentDoesNotOverrideTheSuspectVeto()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());

        using var db = arena.Open();
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = dates[^1], StrategyId = "cand:win", Status = "suspect", RunKind = "live", TriggerJson = "{}",
        });
        db.SaveChanges();
        SeedAck(db, "cand:win", dates[^1], "operator acknowledgment - must not launder a Suspect");

        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        Assert.DoesNotContain(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win");
    }

    /// <summary>
    /// THE RAIL IS FORWARD-ONLY, and this fixture is why the D64 plant machinery still works. Replay has
    /// no operator, and 123 of the frozen generation's 144 promotions were made under Warning — a rail
    /// applied to both channels would refuse 85% of calibration's own promotions. Same run-kind carve-out
    /// D37 already makes for the strategies.status mutation.
    /// </summary>
    [Fact]
    public void D157_InReplayAWarningPromotesWithoutAnAck_BecauseReplayHasNoOperator()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray(), runKind: "replay");
        arena.SeedStrategy("cand:win", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray(), runKind: "replay");

        using var db = arena.Open();
        db.OverfittingStatus.Add(new OverfittingStatusRow
        {
            AsOf = dates[^1], StrategyId = "cand:win", Status = "warning", RunKind = "replay", TriggerJson = "{}",
        });
        db.SaveChanges();

        new EvaluationStep(db, new GateOptions()).Run(dates[^1], runKind: "replay");

        Assert.Contains(db.GoLiveLog.ToList(), g => g.Promoted == "cand:win" && g.RunKind == "replay");
    }
}
