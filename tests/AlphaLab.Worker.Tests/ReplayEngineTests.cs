using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The Arena Replay engine core (FR-19, D95, checkpoint 4.4): the whole pipeline over a historical
/// window under run_kind='replay' at the frozen real watermark. Deterministic (two runs byte-identical;
/// a perturbed store diverges), quarantined (forward projections byte-identical with replay rows
/// present), one generation per arena (mixed vintages refused; --reset deletes only replay rows), and
/// reachable via both the `replay-calibrate` verb's runner and the jobs.kind='replay' executor.
///
/// The replay window sits INSIDE the harness's pre-seeded history (sessions 25..30 — enough trailing
/// sessions for the ADV/vol windows), which is exactly the D70-backfilled shape: bars already in the
/// store, observed at the backfill instant, replayed on the date axis.
/// </summary>
public class ReplayEngineTests
{
    internal static IConfiguration HarnessConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Populations:Size"] = "6",
            ["Populations:CostFreeSize"] = "3",
            // CI-scale plants (FR-36): the same code paths as the full-scale 50-seed cohorts, kept small.
            ["Calibration:Plant:SeedsPerPlant"] = "2",
        }).Build();

    private static ReplayRunner Runner(PipelineHarness h) =>
        new(HarnessConfiguration(), new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, NullLoggerFactory.Instance);

    private static string Conn(PipelineHarness h) => $"Data Source={h.DbPath}";

    private static ReplayRequest Window(PipelineHarness h, bool reset = false) =>
        new(h.Sessions[25], h.Sessions[30], Reset: reset);

    /// <summary>The replay generation's comparable content, rendered to canonical strings (the
    /// CommittedDay idea, replay-side): decisions, trades, equity and population draws, ordered by
    /// natural keys so the comparison is over CONTENT, never insertion order.</summary>
    private static string ReplayContent(PipelineHarness h)
    {
        using var db = h.Open();
        string Render(object o) => JsonSerializer.Serialize(o);
        var parts = new List<string>();
        parts.AddRange(db.Decisions.Where(d => d.RunKind == "replay")
            .OrderBy(d => d.AsOf).ThenBy(d => d.AccountId).AsEnumerable()
            .Select(d => Render(new { d.AsOf, d.AccountId, d.StageJson })));
        parts.AddRange(db.Trades.Where(t => t.RunKind == "replay")
            .OrderBy(t => t.FilledOn).ThenBy(t => t.AccountId).ThenBy(t => t.SecurityId).ThenBy(t => t.Side).AsEnumerable()
            .Select(t => Render(new { t.FilledOn, t.AccountId, t.SecurityId, t.Side, t.Shares, t.RawFillPrice, t.Commission, t.SpreadCost, t.ImpactCost })));
        parts.AddRange(db.EquityCurve.Where(e => e.RunKind == "replay")
            .OrderBy(e => e.AsOf).ThenBy(e => e.AccountId).AsEnumerable()
            .Select(e => Render(new { e.AsOf, e.AccountId, e.Equity, e.Cash })));
        parts.AddRange(db.ControlEquity.Where(c => c.RunKind == "replay")
            .OrderBy(c => c.AsOf).ThenBy(c => c.PopulationId).ThenBy(c => c.MemberIndex).AsEnumerable()
            .Select(c => Render(new { c.AsOf, c.PopulationId, c.MemberIndex, c.Equity })));
        return string.Join("\n", parts);
    }

    /// <summary>Every FORWARD-facing projection, serialized — what the quarantine must hold invariant.</summary>
    private static string ForwardContent(PipelineHarness h)
    {
        using var db = h.Open();
        string Render(object o) => JsonSerializer.Serialize(o);
        var parts = new List<string>();
        parts.AddRange(db.Runs.Where(r => r.RunKind != "replay").OrderBy(r => r.RunId).AsEnumerable()
            .Select(r => Render(new { r.AsOf, r.RunKind, r.Watermark, r.Status })));
        parts.AddRange(db.EquityCurve.Where(e => e.RunKind == "live").OrderBy(e => e.AsOf).ThenBy(e => e.AccountId).AsEnumerable()
            .Select(e => Render(new { e.AsOf, e.AccountId, e.Equity, e.Cash })));
        parts.AddRange(db.Decisions.Where(d => d.RunKind == "live").OrderBy(d => d.AsOf).ThenBy(d => d.AccountId).AsEnumerable()
            .Select(d => Render(new { d.AsOf, d.AccountId, d.StageJson })));
        parts.AddRange(db.ControlEquity.Where(c => c.RunKind == "live").OrderBy(c => c.AsOf).ThenBy(c => c.PopulationId).ThenBy(c => c.MemberIndex).AsEnumerable()
            .Select(c => Render(new { c.AsOf, c.PopulationId, c.MemberIndex, c.Equity })));
        parts.AddRange(db.RegimeLabels.Where(l => l.RunKind == "live").OrderBy(l => l.AsOf).AsEnumerable()
            .Select(l => Render(new { l.AsOf, l.Label, l.InputsHash })));
        parts.AddRange(db.Positions.OrderBy(p => p.AccountId).ThenBy(p => p.SecurityId).AsEnumerable()
            .Where(p => db.Accounts.Any(a => a.AccountId == p.AccountId && a.RunKind == "live"))
            .Select(p => Render(new { p.AccountId, p.SecurityId, p.Shares, p.CostBasis })));
        return string.Join("\n", parts);
    }

    [Fact]
    public async Task FX_ReplayDeterminism_TwoRunsByteIdentical_AndPerturbedStoreDiverges()
    {
        using var h = new PipelineHarness();

        var first = await Runner(h).RunAsync(Conn(h), Window(h));
        Assert.False(first.StoppedEarly);
        Assert.Equal(6, first.SessionsCommitted);          // sessions 25..30 inclusive (weekdays)
        var contentA = ReplayContent(h);
        Assert.NotEmpty(contentA);

        // Same window, same watermark, fresh generation ⇒ byte-identical (NFR-1 for the replay engine).
        var second = await Runner(h).RunAsync(Conn(h), Window(h, reset: true));
        Assert.False(second.StoppedEarly);
        Assert.Equal(first.Watermark, second.Watermark);
        Assert.Equal(contentA, ReplayContent(h));

        // Perturb ONE input the window prices on — as a NEW bar VERSION observed inside the frozen
        // watermark (rule 3: append-only; nothing is updated). The replay must diverge, else the
        // determinism check is measuring nothing.
        using (var db = h.Open())
        {
            var bar = db.Bars.Single(b => b.SecurityId == PipelineHarness.MemberA && b.Date == h.Sessions[26] && b.Version == 1);
            db.Bars.Add(new BarRow
            {
                SecurityId = bar.SecurityId, Date = bar.Date, Version = 2, ObservedAt = bar.ObservedAt,
                Open = bar.Open, High = (bar.High ?? 0) + 25.0, Low = bar.Low, Close = (bar.Close ?? 0) + 25.0,
                AdjClose = (bar.AdjClose ?? 0) + 25.0, Volume = bar.Volume, Source = bar.Source,
            });
            db.SaveChanges();
        }
        var third = await Runner(h).RunAsync(Conn(h), Window(h, reset: true));
        Assert.False(third.StoppedEarly);
        Assert.NotEqual(contentA, ReplayContent(h));
    }

    [Fact]
    public async Task FX_ReplayQuarantine_ForwardProjectionsInvariant_WithReplayRowsPresent()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        await h.RunAsync(h.Run3);
        var forwardBefore = ForwardContent(h);

        var outcome = await Runner(h).RunAsync(Conn(h), Window(h));
        Assert.False(outcome.StoppedEarly);

        using (var db = h.Open())
        {
            // Replay rows genuinely exist across the judged tables…
            Assert.True(db.Runs.Count(r => r.RunKind == "replay" && r.Status == "ok") == 6);
            Assert.NotEmpty(db.EquityCurve.Where(e => e.RunKind == "replay").ToList());
            Assert.NotEmpty(db.ControlEquity.Where(c => c.RunKind == "replay").ToList());
            Assert.NotEmpty(db.Accounts.Where(a => a.RunKind == "replay").ToList());
        }

        // …and every forward projection is byte-identical (F-QUAR at the engine level: the replay
        // engine can write ONLY quarantined rows).
        Assert.Equal(forwardBefore, ForwardContent(h));
    }

    [Fact]
    public async Task FR19_ReplayRunRows_CarryFrozenWatermark_NeverASessionFiction()
    {
        using var h = new PipelineHarness();
        var outcome = await Runner(h).RunAsync(Conn(h), Window(h));

        // W_replay = MAX(observed_at) over the store = the harness pre-seed instant — one REAL
        // watermark on every replay run, never the per-day {asOf}T22:00:00Z reconstruction.
        var expected = $"{h.Sessions[PipelineHarnessPreSeedBoundary(h)]}T22:00:00Z";
        Assert.Equal(expected, outcome.Watermark);
        using var db = h.Open();
        var runs = db.Runs.Where(r => r.RunKind == "replay").ToList();
        Assert.All(runs, r => Assert.Equal(expected, r.Watermark));
        Assert.All(runs, r => Assert.NotEqual($"{r.AsOf}T22:00:00Z", r.Watermark));
    }

    // The harness pre-seeds sessions 0..39 with observed_at = Sessions[39]'s... the SEED stamp is the
    // session BEFORE the first run day: Sessions[PreSeedCount-1] = Sessions[39].
    private static int PipelineHarnessPreSeedBoundary(PipelineHarness h) => 39;

    [Fact]
    public void FR19_ReplayDateCeiling_FutureActionInvisible()
    {
        using var h = new PipelineHarness();
        using var db = h.Open();
        // Two actions visible at the frozen watermark: one effective before the simulated day, one after.
        new CorporateActionIngestion(db).IngestDividends(PipelineHarness.MemberA,
        [
            new DividendEvent(h.Sessions[26], 0.10m, 0.10m),
            new DividendEvent(h.Sessions[35], 0.20m, 0.20m),
        ], $"{h.Sessions[39]}T22:00:00Z");

        var simDay = new ReplaySimDay();
        simDay.Advance(h.Sessions[28]);
        var ceiling = new DateCeilingCorporateActionReads(new CorporateActionReadService(db), simDay);

        // The raw read at the frozen watermark sees BOTH (they were all observed by then); the ceiling
        // hides the one a 2015-style observer could not have known (D95's date axis).
        var raw = new CorporateActionReadService(db).GetActionsAsOf(PipelineHarness.MemberA, $"{h.Sessions[39]}T22:00:00Z");
        Assert.Equal(2, raw.Count);
        var seen = ceiling.GetActionsAsOf(PipelineHarness.MemberA, $"{h.Sessions[39]}T22:00:00Z");
        var only = Assert.Single(seen);
        Assert.Equal(h.Sessions[26], only.EffectiveDate);
    }

    [Fact]
    public async Task D95_Reset_RefusesMixedVintage_DeletesOnlyReplayRows()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);   // one forward day, so "forward untouched" is observable
        var forwardBefore = ForwardContent(h);

        var natural = await Runner(h).RunAsync(Conn(h), Window(h));
        Assert.False(natural.StoppedEarly);

        // A DIFFERENT watermark against the committed generation is refused (mixed vintages would
        // poison the D56 curves) — nothing about the store changes.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner(h).RunAsync(Conn(h), Window(h) with { Watermark = $"{h.Sessions[38]}T22:00:00Z" }));
        Assert.Contains("mixed replay vintages", ex.Message, StringComparison.Ordinal);

        // --reset deletes the generation — and ONLY the generation: forward rows byte-identical, and
        // the fresh run rebuilds a complete one.
        var fresh = await Runner(h).RunAsync(Conn(h), Window(h, reset: true));
        Assert.False(fresh.StoppedEarly);
        Assert.Equal(6, fresh.SessionsCommitted);
        Assert.Equal(forwardBefore, ForwardContent(h));

        using var db = h.Open();
        Assert.Equal(6, db.Runs.Count(r => r.RunKind == "replay"));
    }

    [Fact]
    public async Task D95_Resume_SkipsCommittedDays_SameWatermark()
    {
        using var h = new PipelineHarness();
        var first = await Runner(h).RunAsync(Conn(h), Window(h));
        Assert.Equal(6, first.SessionsCommitted);
        var contentA = ReplayContent(h);

        // Re-running the SAME command is a resume: everything already committed is skipped, nothing
        // is rewritten (crash-resumability without a reset).
        var again = await Runner(h).RunAsync(Conn(h), Window(h));
        Assert.Equal(0, again.SessionsCommitted);
        Assert.Equal(6, again.SessionsSkippedAlreadyCommitted);
        Assert.Equal(contentA, ReplayContent(h));
    }

    // Phase-4 review: --reset deletes the replay trials rows but keeps the shared strategies rows, so
    // a trials add gated on the STRATEGIES existence check left every post-reset generation with
    // trialsCount = 0 (S2 deflation at N=1 instead of N=plants) — and a reset arena silently produced
    // different calibration artifacts than a fresh one at identical (inputs, watermark, seeds).
    [Fact]
    public async Task D95_ResetThenRerun_ReseedsReplayTrials_SameCountAsFreshArena()
    {
        using var h = new PipelineHarness();

        var first = await Runner(h).RunAsync(Conn(h), Window(h));
        Assert.False(first.StoppedEarly);
        int freshTrials;
        using (var db = h.Open())
        {
            freshTrials = db.TrialsRegistry.Count(t => t.RunKind == "replay");
            Assert.True(freshTrials > 0, "a plants replay must register replay trials");
        }

        var again = await Runner(h).RunAsync(Conn(h), Window(h, reset: true));
        Assert.False(again.StoppedEarly);
        using (var db = h.Open())
        {
            Assert.Equal(freshTrials, db.TrialsRegistry.Count(t => t.RunKind == "replay"));
        }
    }

    // Phase-4 review: the one-generation guard must refuse a MODE mix at the same watermark, not just
    // a vintage mix — interleaved seeding (no plants, no evaluation) and calibration days corrupt the
    // cadence bookkeeping and punch holes in the plant equity tracks the D56 curves are built from.
    [Fact]
    public async Task D95_SeedingThenCalibrate_SameWatermark_RefusesModeMix()
    {
        using var h = new PipelineHarness();

        var seeding = await Runner(h).RunAsync(Conn(h),
            new ReplayRequest(h.Sessions[25], h.Sessions[27], WithPlants: false, WithEvaluation: false));
        Assert.False(seeding.StoppedEarly);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner(h).RunAsync(Conn(h), Window(h)));
        Assert.Contains("WITHOUT plants", ex.Message, StringComparison.Ordinal);

        // The reverse order refuses too: a seeding run must not extend an evaluated generation.
        var calibrated = await Runner(h).RunAsync(Conn(h), Window(h, reset: true));
        Assert.False(calibrated.StoppedEarly);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner(h).RunAsync(Conn(h),
                new ReplayRequest(h.Sessions[31], h.Sessions[33], WithPlants: false, WithEvaluation: false)));
        Assert.Contains("WITH plants", ex2.Message, StringComparison.Ordinal);
    }

    // Verify-pass completion of the mode guard: a calibration that seeded plants and then aborted
    // before ANY day committed leaves plant accounts with zero ok runs — a seeding backtest must
    // still be refused, not slip past the empty-generation early return and interleave.
    [Fact]
    public async Task D95_AbortedSeededGeneration_StillRefusesASeedingBacktest()
    {
        using var h = new PipelineHarness();
        using (var db = h.Open())
        {
            db.Accounts.Add(new AccountRow
            {
                StrategyId = "plant:edge:daily:2:0", StartingCash = 100_000m, RunKind = "replay",
            });
            db.SaveChanges();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner(h).RunAsync(Conn(h),
                new ReplayRequest(h.Sessions[25], h.Sessions[27], WithPlants: false, WithEvaluation: false)));
        Assert.Contains("--reset", ex.Message, StringComparison.Ordinal);
    }

    // Verify-pass completion of the forward-status decoupling: the evaluation-cadence trigger is
    // run-kind-scoped, so forward-retiring every real strategy must not silently disable a plantless
    // replay generation's evaluations (the fifth raw-status consumer).
    [Fact]
    public async Task D95_ForwardRetire_NeverDisablesTheReplayEvaluationCadence()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);   // seeds the shared roster rows
        using (var db = h.Open())
        {
            foreach (var s in db.Strategies.Where(s => s.Status == "candidate" || s.Status == "live"))
            {
                s.Status = "retired";   // the forward monitor's by-design mutation
            }
            db.SaveChanges();
        }

        var outcome = await Runner(h).RunAsync(Conn(h),
            new ReplayRequest(h.Sessions[5], h.Sessions[35], WithPlants: false, WithEvaluation: true));
        Assert.False(outcome.StoppedEarly);

        using (var db = h.Open())
        {
            // The replay cadence fired and judged its candidate-based roster despite the forward retires.
            Assert.True(db.OverfittingStatus.Any(o => o.RunKind == "replay"),
                "the replay evaluation cadence must run against the seeded-role roster");
        }
    }

    // Phase-4 review: zero sessions is a FAILURE (rule 10) — an unseeded calendar or a from/to typo
    // must not mark a replay job 'done' having done nothing, and must not seed plants either.
    [Fact]
    public async Task D95_ZeroSessionWindow_FailsClosed_AndSeedsNothing()
    {
        using var h = new PipelineHarness();

        var outcome = await Runner(h).RunAsync(Conn(h), new ReplayRequest("2030-01-06", "2030-01-10"));
        Assert.True(outcome.StoppedEarly);
        Assert.Contains("no sessions", outcome.StopReason, StringComparison.Ordinal);

        using var db = h.Open();
        Assert.Empty(db.Runs.Where(r => r.RunKind == "replay").ToList());
        Assert.Empty(db.Accounts.Where(a => a.RunKind == "replay").ToList());
    }

    // Phase-4 review (D59): the replay-calibrate CLI verb dispatches before the host's
    // StaleRunRecovery guard exists, so ReplayRunner carries its own sole-writer gate — a FRESH
    // heartbeat under run_in_progress=1 refuses; a stale one is a crash orphan and is cleared.
    [Fact]
    public async Task D59_Replay_RefusesWhileAnotherWriterIsLive_ClearsAStaleOne()
    {
        using var h = new PipelineHarness();

        using (var db = h.Open())
        {
            var state = db.WorkerState.Single(w => w.Id == 1);
            state.RunInProgress = 1;
            state.HeartbeatAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture); // fresh — a live writer
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<OverlappingWriterException>(() => Runner(h).RunAsync(Conn(h), Window(h)));

        using (var db = h.Open())
        {
            var state = db.WorkerState.Single(w => w.Id == 1);
            state.HeartbeatAt = "2020-01-01T00:00:00Z"; // long stale — a crashed writer
            db.SaveChanges();
        }
        var outcome = await Runner(h).RunAsync(Conn(h), Window(h));
        Assert.False(outcome.StoppedEarly);
        using (var db = h.Open())
        {
            Assert.Equal(0, db.WorkerState.Single(w => w.Id == 1).RunInProgress);
        }
    }

    [Fact]
    public async Task FX_JobDrain_ReplayExecutor_RunsAQueuedReplayJob()
    {
        // The drain WIRING (queued → executor → done/failed) is FX-JobDrain (Phase 2); what is new here
        // is that a jobs.kind='replay' row EXECUTES through the same ReplayRunner the verb drives —
        // the API's 202+job_id path is real, not a stub that would fail closed in the drainer.
        using var h = new PipelineHarness();
        using (var db = h.Open())
        {
            db.Jobs.Add(new JobRow
            {
                Kind = "replay",
                Status = "queued",
                SubmittedAt = "2026-07-22T00:00:00Z",
                RequestJson = JsonSerializer.Serialize(Window(h)),
            });
            db.SaveChanges();
        }

        var executor = new ReplayJobExecutor(
            HarnessConfiguration(), new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, Conn(h), NullLoggerFactory.Instance);
        Assert.Equal("replay", executor.Kind);   // the CHECK-constrained jobs.kind this executor claims

        using (var db = h.Open())
        {
            var job = db.Jobs.Single(j => j.Kind == "replay");
            await executor.ExecuteAsync(job, CancellationToken.None);
        }

        using (var db2 = h.Open())
        {
            Assert.Equal(6, db2.Runs.Count(r => r.RunKind == "replay" && r.Status == "ok"));
        }
    }
}
