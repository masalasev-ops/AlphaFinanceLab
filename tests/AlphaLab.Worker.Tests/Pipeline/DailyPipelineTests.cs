using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Pipeline;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// FX-StagedPipeline — the D53 staged daily pipeline (checkpoint 2.10). The run-row lifecycle, the
/// zero-write Stage 1, the fail-closed gate abort, the D77 flag persistence, and the decide-at-close-T /
/// fill-at-open-T+1 mechanics against the real ledger.
/// </summary>
public class DailyPipelineTests
{
    // ---- Stage 1 is write-incapable by CONSTRUCTION ----
    [Fact]
    public void Stage1Fetch_TakesNoDbContext_SoItCannotWrite()
    {
        var ctor = Assert.Single(typeof(Stage1Fetch).GetConstructors());
        Assert.DoesNotContain(ctor.GetParameters(), p => p.ParameterType == typeof(AlphaLabDbContext));
    }

    // ---- Stage 1 zero-write, proven at runtime: a provider that hard-fails leaves EVERY table empty ----
    [Fact]
    public async Task Stage1_ProviderHardFails_NoRunRow_NothingWritten()
    {
        using var h = new PipelineHarness();
        h.Market.ThrowOnFetch = true;

        await Assert.ThrowsAnyAsync<Exception>(() => h.RunAsync(h.Run1));

        using var db = h.Open();
        Assert.Empty(db.Runs);                                  // FR-29: a Stage-1 failure writes literally nothing
        Assert.Empty(db.Strategies);                            // DummyRoster.Seed lives in Stage 2 — never reached
        Assert.Empty(db.Trades);
        Assert.DoesNotContain(db.Bars.ToList(), b => b.Date == h.Run1); // no run-day bar ingested
        Assert.Equal(0, db.WorkerState.Single().RunInProgress); // run_in_progress never set (no run opened)
    }

    // ---- A fail-closed reject aborts BEFORE the run row is written ----
    [Fact]
    public async Task Stage1_GateRejects_AbortsBeforeRunRow_NothingWritten()
    {
        using var h = new PipelineHarness();
        // A non-positive close on the run day ⇒ the FR-6 gate rejects (rule 10).
        h.Market.SetBar(PipelineHarness.MemberASymbol, new EodBar(h.Run1, 100, 100, 100, -5.0, -5.0, 10_000_000));

        var result = await h.RunAsync(h.Run1);

        Assert.True(result.Aborted);
        Assert.False(result.Committed);
        Assert.Null(result.RunId);

        using var db = h.Open();
        Assert.Empty(db.Runs);
        Assert.DoesNotContain(db.Bars.ToList(), b => b.Date == h.Run1 && b.SecurityId == PipelineHarness.MemberA);
    }

    // ---- Happy path: the four-step lifecycle lands a committed 'ok' run with run_in_progress cleared ----
    [Fact]
    public async Task RunDay_HappyPath_RunRowOk_RunInProgressCleared_CurrentRunIdSet()
    {
        using var h = new PipelineHarness();

        var result = await h.RunAsync(h.Run1);

        Assert.True(result.Committed);
        Assert.False(result.Aborted);
        Assert.NotNull(result.RunId);

        using var db = h.Open();
        var run = Assert.Single(db.Runs);
        Assert.Equal("ok", run.Status);
        Assert.Equal(h.Run1, run.AsOf);
        Assert.Equal("live", run.RunKind);
        Assert.Equal($"{h.Run1}T22:00:00Z", run.Watermark);       // session-derived, never UtcNow
        Assert.NotNull(run.FinishedAt);

        var state = db.WorkerState.Single();
        Assert.Equal(0, state.RunInProgress);                     // step 4 cleared it
        Assert.Equal(run.RunId, state.CurrentRunId);
    }

    // ---- D77: a gate WARN rides through Stage 1 and lands under the run_id in Stage 2 ----
    [Fact]
    public async Task RunDay_PersistsGateWarn_UnderRunId()
    {
        using var h = new PipelineHarness();
        // A 700%+ single-day jump on the run day ⇒ a robust-z outlier WARN (does not abort; is persisted).
        h.Market.SetBar(PipelineHarness.MemberBSymbol, new EodBar(h.Run1, 119.5, 1000, 119.5, 1000, 1000, 10_000_000));

        var result = await h.RunAsync(h.Run1);
        Assert.True(result.Committed);

        using var db = h.Open();
        var outlier = Assert.Single(db.DataQualityFlags.Where(f => f.Issue == "outlier_return").ToList());
        Assert.Equal(result.RunId, outlier.RunId);                // D77: run_id NOT NULL, stamped by Stage 2
        Assert.Equal("warn", outlier.Severity);
        Assert.Equal(PipelineHarness.MemberB, outlier.SecurityId);
    }

    // ---- A catch-up run writes catchup_log; a live run does not ----
    [Fact]
    public async Task RunDay_Catchup_WritesCatchupLog()
    {
        using var h = new PipelineHarness();

        var result = await h.RunAsync(h.Run1, "catchup");

        using var db = h.Open();
        var log = Assert.Single(db.CatchupLog);
        Assert.Equal(h.Run1, log.AsOf);
        Assert.Equal(result.RunId, log.RunId);
        Assert.Equal("catchup", db.Runs.Single().RunKind);
    }

    // ---- The roster is seeded (idempotently) and every dummy account gets a day-one equity point ----
    [Fact]
    public async Task RunDay_SeedsDummyRoster_ThreeAccounts_DayOneEquityIsStartingCash()
    {
        using var h = new PipelineHarness();

        await h.RunAsync(h.Run1);

        using var db = h.Open();
        Assert.Equal(3, db.Strategies.Count());
        Assert.Equal(3, db.Accounts.Count());
        Assert.Equal(3, db.Decisions.Count(d => d.AsOf == h.Run1));   // every account records its decision
        Assert.Empty(db.Trades);                                       // orders decided at run1 fill at run2 — no trade yet

        var dayOne = db.EquityCurve.Where(e => e.AsOf == h.Run1).ToList();
        Assert.Equal(3, dayOne.Count);
        Assert.All(dayOne, e => Assert.Equal(100_000m, e.Equity));     // no positions yet ⇒ equity == deposit
    }

    // ---- decide-at-close-T, fill-at-open-T+1: run1 decides, run2 fills ----
    [Fact]
    public async Task RunDay_DecidesThenFillsNextSession_TPlus1()
    {
        using var h = new PipelineHarness();

        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        using var db = h.Open();
        var trades = db.Trades.ToList();
        Assert.NotEmpty(trades);
        Assert.All(trades, t => Assert.Equal("buy", t.Side));
        Assert.All(trades, t => Assert.Equal(h.Run1, t.DecidedOn));   // decided at run1's close
        Assert.All(trades, t => Assert.Equal(h.Run2, t.FilledOn));    // filled at run2's open
        Assert.All(trades, t => Assert.Equal("cm-1.0", t.CostModelVersion)); // D43 stamp
        Assert.All(trades, t => Assert.True(t.SpreadCost > 0m));      // costs always on
        Assert.NotEmpty(db.Positions);

        // The cap-weight account holds exactly its single proxy after the fill.
        var cw = db.Accounts.Single(a => a.StrategyId == "buyhold:cw").AccountId;
        var cwPosition = Assert.Single(db.Positions.Where(p => p.AccountId == cw).ToList());
        Assert.Equal(PipelineHarness.CwProxy, cwPosition.SecurityId);
    }

    // ---- A dividend on a held name credits cash on the ex-date; the equity curve stays continuous ----
    [Fact]
    public async Task RunDay_DividendOnHeldName_CreditsCash()
    {
        using var h = new PipelineHarness();
        h.Market.AddDividend(PipelineHarness.MemberASymbol, new DividendEvent(h.Run3, 1.50m, 1.50m));

        await h.RunAsync(h.Run1);   // EW decides to open both members
        await h.RunAsync(h.Run2);   // EW fills — now holds MEMBERA
        await h.RunAsync(h.Run3);   // dividend ex-date: MEMBERA held ⇒ credited

        using var db = h.Open();
        var ew = db.Accounts.Single(a => a.StrategyId == "buyhold:ew").AccountId;
        Assert.NotEmpty(db.CashEvents.Where(c => c.AccountId == ew && c.Type == "dividend").ToList());
        Assert.Equal(3, db.EquityCurve.Count(e => e.AccountId == ew)); // a point every run day
    }

    // ---- ux_runs_ok_forward is the backstop: a second 'ok' forward run of the same day is rejected ----
    [Fact]
    public async Task RunDay_ReRunOfOkForwardDay_IsRejectedByTheUniqueIndex()
    {
        using var h = new PipelineHarness();

        await h.RunAsync(h.Run1); // ok
        await Assert.ThrowsAnyAsync<Exception>(() => h.RunAsync(h.Run1)); // finalising a 2nd 'ok' row conflicts

        using var db = h.Open();
        Assert.Single(db.Runs.Where(r => r.AsOf == h.Run1 && r.Status == "ok").ToList());
    }

    // ---- An account whose strategy_id this build cannot run is skipped, not crashed (rule 10) ----
    [Fact]
    public async Task RunDay_UnknownStrategyAccount_Skipped_RunStillCommits()
    {
        using var h = new PipelineHarness();
        using (var db = h.Open())
        {
            db.Strategies.Add(new StrategyRow
            {
                StrategyId = "mystery:1", Family = "x", ConfigJson = "{}", ExitPolicyJson = "{}",
                CreatedOn = h.Run1, Status = "candidate",
            });
            db.SaveChanges();
            new LedgerStore(db).OpenAccount(
                new Account { StrategyId = "mystery:1", StartingCash = 100_000m, RunKind = RunKind.Live }, h.Run1);
        }

        var result = await h.RunAsync(h.Run1);
        Assert.True(result.Committed);

        using var db2 = h.Open();
        var mystery = db2.Accounts.Single(a => a.StrategyId == "mystery:1").AccountId;
        Assert.Empty(db2.EquityCurve.Where(e => e.AccountId == mystery).ToList()); // skipped before recording anything
        Assert.Equal(3, db2.EquityCurve.Count(e => e.AsOf == h.Run1));             // the three dummies still ran
    }

    // ---- D84 / finding 190: end to end, the pipeline sizes new opens against CASH (not equity), so an
    //      account never takes on STRUCTURAL leverage — it never spends the value of a held position (the
    //      pre-fix "size against equity" bug). The only way cash dips below zero is the unavoidable trading
    //      COSTS on the fills — a bounded residual deferred to finding 196 — never a fraction of the held
    //      book. (The discriminating over-spend scenario is proven at the funnel level,
    //      FunnelRunnerTests.D84_AnAccumulatingOpensOnlyBook_NeverRatchetsPastItsCash.) ----
    [Fact]
    public async Task RunDay_AcrossSessions_CashNeverBelowTradingCosts_NoStructuralLeverage()
    {
        using var h = new PipelineHarness();
        h.Market.AddDividend(PipelineHarness.MemberASymbol, new DividendEvent(h.Run3, 1.50m, 1.50m));

        await h.RunAsync(h.Run1); // decide opens
        await h.RunAsync(h.Run2); // fill — the accounts invest their cash
        await h.RunAsync(h.Run3); // dividend credits cash; an equity point is recorded each day

        using var db = h.Open();
        foreach (var account in db.Accounts.ToList())
        {
            var costs = db.Trades.Where(t => t.AccountId == account.AccountId).ToList()
                .Sum(t => t.Commission + t.SpreadCost + t.ImpactCost);
            var curve = db.EquityCurve.Where(e => e.AccountId == account.AccountId).OrderBy(e => e.AsOf).ToList();
            var latestCash = curve[^1].Cash;

            // The invariant: cash never drops below −(cumulative trading costs). A pre-fix overspend
            // (sizing against equity) would drive it far below that, by a fraction of the held book.
            Assert.True(latestCash >= -costs - 0.01m,
                $"account {account.AccountId}: cash {latestCash} is below −(trading costs {costs}) — structural leverage, not the cost residual.");
        }
    }
}
