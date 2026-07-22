using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-ReproduceDay (TEST_PLAN §3) — the NFR-1 proof (FR-25 / checkpoint 3.5.1). MASTER §13.5 claims
/// any historical run is reproducible forever against exactly the data it saw; these tests are what
/// make that claim falsifiable.
///
/// Driven over the harness's REAL migrated on-disk arena through the REAL DI graph and the REAL
/// DailyPipeline — only the external data providers are faked, which is what makes the inputs
/// deterministic in the first place. A determinism suite that only ever passes proves nothing, so the
/// perturbation and frozen-book cases matter as much as the happy path.
/// </summary>
public class ReproduceDayTests
{
    // The reproduce runner composes its own graph from configuration, so these values MUST match the
    // harness's registered options — a mismatch changes the population size and the day genuinely
    // stops reproducing. That coupling is the point: the reproduction must run the same pipeline.
    private static IConfiguration HarnessConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Populations:Size"] = "6",
            ["Populations:CostFreeSize"] = "3",
        }).Build();

    private static ReproduceDayRunner RunnerFor(PipelineHarness h) =>
        new(HarnessConfiguration(),
            new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" },
            NullLoggerFactory.Instance);

    private static string ConnectionFor(PipelineHarness h) => $"Data Source={h.DbPath}";

    [Fact]
    public async Task FR25_ReproduceDay_CommittedDay_IsByteIdentical()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        await h.RunAsync(h.Run3);

        // Reproduce a day with history BEHIND it (Run2, not Run1): the book it starts from is a real
        // carried-forward book, so the D90 snapshot is genuinely load-bearing here.
        var outcome = await RunnerFor(h).RunAsync(ConnectionFor(h), h.Run2);

        Assert.True(outcome.Matches, string.Join("\n", outcome.Differences));
        Assert.Equal($"{h.Run2}T22:00:00Z", outcome.Watermark);
        Assert.Empty(outcome.Differences);
    }

    [Fact]
    public async Task FR25_ReproduceDay_PerturbedInput_Diverges()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        await h.RunAsync(h.Run3);

        var runner = RunnerFor(h);
        // Corrupt ONE input the day priced on, in the scratch copy only: the prior session's close for
        // MemberA, which feeds the score, the sizing and the mark. If the day still "reproduced" after
        // this, the comparison would be measuring nothing.
        runner.PerturbScratch = db =>
        {
            var bar = db.Bars.Single(b => b.SecurityId == PipelineHarness.MemberA && b.Date == h.Run1);
            bar.Close += 25.0;
            bar.AdjClose += 25.0;
            db.SaveChanges();
        };

        var outcome = await runner.RunAsync(ConnectionFor(h), h.Run2);

        Assert.False(outcome.Matches);
        Assert.NotEmpty(outcome.Differences);
    }

    [Fact]
    public async Task FR25_ReproduceDay_FrozenPositionDay_IsByteIdentical()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        // Freeze a held name (the D39/D86 unmapped-halt state). A production freeze happens INSIDE a
        // run — CorporateActionApplier freezes on a bar stoppage during Stage 2 — so the freezing day's
        // own snapshot records it and the NEXT day reproduces from that. Mirror that shape: freeze,
        // commit Run3 (whose D90 snapshot captures frozen=true), then reproduce Run4, which must
        // restore the frozen book from Run3's snapshot.
        const string reason = "bar stoppage with no mapped corporate action (test freeze)";
        var frozen = FreezeOneHeldPosition(h, reason);
        Assert.NotNull(frozen);

        await h.RunAsync(h.Run3);
        var run4 = h.Sessions[43];      // the session after Run3 (the harness seeds 50)
        await h.RunAsync(run4);

        using (var db = h.Open())
        {
            // The mechanism under test: Run3's snapshot carried the freeze AND its reason forward.
            var snapshot = db.PositionSnapshots.Single(p =>
                p.AccountId == frozen!.Value.Account && p.SecurityId == frozen.Value.Security && p.AsOf == h.Run3);
            Assert.True(snapshot.Frozen);
            Assert.Equal(reason, snapshot.FrozenReason);

            // And the committed Run4 really did render the freeze into its provenance record, verbatim.
            var stageJson = db.Decisions.Single(d => d.AccountId == frozen.Value.Account && d.AsOf == run4).StageJson;
            Assert.Contains(reason, stageJson, StringComparison.Ordinal);
        }

        var outcome = await RunnerFor(h).RunAsync(ConnectionFor(h), run4);

        Assert.True(outcome.Matches, string.Join("\n", outcome.Differences));
    }

    [Fact]
    public async Task FR25_ReproduceDay_LeavesLiveStoreUntouched()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var before = StoreFingerprint(h);
        var outcome = await RunnerFor(h).RunAsync(ConnectionFor(h), h.Run2);
        var after = StoreFingerprint(h);

        Assert.True(outcome.Matches, string.Join("\n", outcome.Differences));
        // D59: the sole writer is the Worker's daily pipeline. reproduce-day opens the arena
        // Mode=ReadOnly and does every write in its throwaway copy.
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task FR25_ReproduceDay_WithoutACommittedRun_FailsClosed()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);

        // Run3 was never run — there is nothing committed to prove the reproduction against.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunnerFor(h).RunAsync(ConnectionFor(h), h.Run3));

        Assert.Contains("No committed forward run", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FR25_ScratchStore_RewindIsComplete_OrThrows()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var scratchPath = Path.Combine(Path.GetTempPath(), $"alphalab-rewind-{Guid.NewGuid():N}.db");
        try
        {
            // The guard reads the store AFTER the rewind, so a row planted by a hypothetical missed
            // table must make it throw rather than let a vacuous comparison proceed.
            using var scratch = ScratchStore.CreateRewound($"Data Source={h.DbPath}", h.Run2, h.Run1, scratchPath);
            using var db = scratch.OpenContext();

            // Rewound clean first: this is the state the re-run is supposed to start from.
            Assert.Empty(db.EquityCurve.Where(e => e.AsOf == h.Run2));
            // ...and the book was restored from the D90 snapshot, not left at the current book.
            var restored = db.Positions.Count();
            var expected = db.PositionSnapshots.Count(p => p.AsOf == h.Run1 && p.RunKind == "live");
            Assert.Equal(expected, restored);
        }
        finally
        {
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(scratchPath + s)) File.Delete(scratchPath + s);
            }
        }
    }

    [Fact]
    public async Task FR25_ScratchStore_PlantedFutureRow_FailsClosed()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        // Simulate the one failure mode the guard exists for: a rewind that missed a table, leaving the
        // target day's own output in place. Done by rewinding to Run2 and then re-planting a Run2 row.
        var scratchPath = Path.Combine(Path.GetTempPath(), $"alphalab-planted-{Guid.NewGuid():N}.db");
        try
        {
            using (var scratch = ScratchStore.CreateRewound($"Data Source={h.DbPath}", h.Run2, h.Run1, scratchPath))
            using (var db = scratch.OpenContext())
            {
                db.EquityCurve.Add(new Data.Entities.EquityCurveRow
                {
                    AccountId = 1, AsOf = h.Run2, Equity = 1m, Cash = 1m, RunKind = "live",
                });
                db.SaveChanges();

                var ex = Assert.Throws<InvalidOperationException>(() => ScratchStoreGuard.Assert(db, h.Run2));
                Assert.Contains("equity_curve", ex.Message, StringComparison.Ordinal);
                Assert.Contains("vacuously", ex.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(scratchPath + s)) File.Delete(scratchPath + s);
            }
        }
    }

    /// <summary>Freeze the first held position we find. Uses the real ledger seam (FreezePosition), so
    /// the fixture state is exactly the state a bar stoppage produces.</summary>
    private static (long Account, long Security)? FreezeOneHeldPosition(PipelineHarness h, string reason)
    {
        using var db = h.Open();
        var position = db.Positions.OrderBy(p => p.AccountId).ThenBy(p => p.SecurityId).FirstOrDefault();
        if (position is null) return null;

        new LedgerStore(db).FreezePosition(position.AccountId, new SecurityId(position.SecurityId), reason);
        return (position.AccountId, position.SecurityId);
    }

    /// <summary>Row counts across every table a run writes, plus the file's length — enough to catch
    /// any write the reproduction might have leaked into the live arena.</summary>
    private static string StoreFingerprint(PipelineHarness h)
    {
        using var db = h.Open();
        return string.Join("|",
            db.Runs.Count(), db.Bars.Count(), db.Trades.Count(), db.Decisions.Count(),
            db.EquityCurve.Count(), db.ControlEquity.Count(), db.Positions.Count(),
            db.PositionSnapshots.Count(), db.CashEvents.Count(), db.RegimeLabels.Count(),
            new FileInfo(h.DbPath).Length);
    }
}
