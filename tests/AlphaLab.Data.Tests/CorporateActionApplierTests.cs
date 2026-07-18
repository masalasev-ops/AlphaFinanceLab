using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// §13.6 part 1 end-to-end against the versioned store: dividend → cash, split → restated position,
/// ticker change → nothing (the D39 non-event), unmapped stoppage → freeze. Actions are resolved at
/// the run's watermark (D76). Fixtures FX-Dividend, FX-Split, FX-Unmapped.
/// </summary>
public class CorporateActionApplierTests
{
    private const long Aapl = 1;
    private static readonly SecurityId AaplId = new(Aapl);
    private const string AsOf = "2026-07-16";
    private const string Watermark = "2026-07-16T22:00:00Z";
    private static readonly string ObservedEarly = "2026-07-16T20:00:00Z"; // visible at the watermark

    private static CorporateActionApplier Applier(AlphaLabDbContext db, CorporateActionsOptions? options = null) => new(
        new LedgerStore(db),
        new CorporateActionReadService(db),
        new BarReadService(db),
        options ?? new CorporateActionsOptions());

    /// <summary>Seed AAPL + an account holding <paramref name="shares"/>, and (optionally) an asOf-day
    /// bar so the stoppage check sees a live name.</summary>
    private static long Seed(AlphaLabDbContext db, double shares = 100, decimal basis = 10_000m, bool withBarToday = true)
    {
        new SecurityMaster(db).Register("AAPL", "US", "2020-01-01"); // security_id 1
        var store = new LedgerStore(db);
        var account = store.OpenAccount(new Account { StrategyId = "bh", StartingCash = 100_000m }, "2026-01-02");
        store.UpsertPosition(new Position
        {
            AccountId = account.AccountId, SecurityId = AaplId, Shares = shares, CostBasis = basis, OpenedOn = "2026-01-02",
        });

        if (withBarToday)
        {
            new BarIngestionService(db).IngestEod(Aapl,
                [new EodBar(AsOf, 149.5, 151.0, 148.0, 150.0, 150.0, 1_000_000)], ObservedEarly);
        }

        return account.AccountId;
    }

    // ============================ FX-Dividend ============================

    [Fact]
    public void FR9_FxDividend_CreditsCashOnExDate_PositionUnchanged()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db);
            new CorporateActionIngestion(db).IngestDividends(Aapl,
                [new DividendEvent(AsOf, Value: 0.24m, UnadjustedValue: 0.25m)], ObservedEarly);

            var outcome = Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            // Cash: 100 shares × 0.25 UNADJUSTED (never the split-adjusted 0.24) = 25.00, on the ex-date.
            var div = Assert.Single(store.GetCashEvents(account, RunKind.Live), e => e.Type == CashEventType.Dividend);
            Assert.Equal(25.00m, div.Amount);
            Assert.Equal(AsOf, div.AsOf);
            Assert.Equal(AaplId, div.SecurityId);
            Assert.NotNull(div.ActionId);

            // Position share count is untouched by a dividend.
            Assert.Equal(100, store.GetPosition(account, AaplId)!.Shares);
            Assert.Contains(outcome.Applied, a => a.Type == CorporateActionType.Dividend);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>A dividend whose ex-date is NOT today is not applied — the applier acts only on the
    /// day's actions, which is what makes one-transaction-per-day cover history exactly once.</summary>
    [Fact]
    public void FR9_ADividendOnAnotherDay_IsNotAppliedToday()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db);
            new CorporateActionIngestion(db).IngestDividends(Aapl,
                [new DividendEvent("2026-07-10", 0.24m, 0.25m)], ObservedEarly); // ex-date last week

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            Assert.DoesNotContain(new LedgerStore(db).GetCashEvents(account, RunKind.Live),
                e => e.Type == CashEventType.Dividend);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>A later-observed dividend is invisible at the run's watermark (D76) — the exact
    /// property that keeps a replay pinned to the past from crediting a dividend seen in the future.</summary>
    [Fact]
    public void FR9_ADividendObservedAfterTheWatermark_IsNotCredited()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db);
            // Observed a week AFTER the run's watermark.
            new CorporateActionIngestion(db).IngestDividends(Aapl,
                [new DividendEvent(AsOf, 0.24m, 0.25m)], "2026-07-23T20:00:00Z");

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            Assert.DoesNotContain(new LedgerStore(db).GetCashEvents(account, RunKind.Live),
                e => e.Type == CashEventType.Dividend);
        }
        finally { TestDb.Delete(path); }
    }

    // ============================ FX-Split ============================

    [Fact]
    public void FR9_FxSplit_MultipliesShares_KeepsBasis_NoCashNoTrade()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 10_000m);
            new CorporateActionIngestion(db).IngestSplits(Aapl,
                [new SplitEvent(AsOf, Ratio: 2.0, RawRatio: "2/1")], ObservedEarly);

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            var position = store.GetPosition(account, AaplId)!;
            Assert.Equal(200, position.Shares);            // × 2
            Assert.Equal(10_000m, position.CostBasis);      // total basis unchanged

            // A split is neither a trade nor a cash event.
            Assert.Empty(store.GetTrades(account, RunKind.Live));
            Assert.DoesNotContain(store.GetCashEvents(account, RunKind.Live), e => e.Type != CashEventType.Deposit);
        }
        finally { TestDb.Delete(path); }
    }

    // ============================ Ticker change — the D39 non-event ============================

    /// <summary>A ticker change causes ZERO ledger churn: no trade, no position change. The alias
    /// moved in ticker_history; the position kept its security_id. This is D39 made a tested fact.</summary>
    [Fact]
    public void FR9_TickerChange_CausesZeroLedgerChurn()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 10_000m);
            // SecurityMaster writes the ticker_change corporate_actions row (and moves the alias).
            new SecurityMaster(db).RecordTickerChange(Aapl, "APLX", AsOf, ObservedEarly);

            var outcome = Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            var position = store.GetPosition(account, AaplId)!;
            Assert.Equal(100, position.Shares);             // unchanged
            Assert.Equal(10_000m, position.CostBasis);       // unchanged
            Assert.Empty(store.GetTrades(account, RunKind.Live));         // no phantom churn
            Assert.Contains(outcome.Applied, a => a.Type == CorporateActionType.TickerChange);
        }
        finally { TestDb.Delete(path); }
    }

    // ============================ FX-Unmapped (fail closed) ============================

    /// <summary>A held name with no bar today and no action to explain it FREEZES at the last print
    /// (§13.6/rule 10) — never carried at a stale price.</summary>
    [Fact]
    public void FR9_FxUnmapped_HeldNameWithNoBarAndNoEvent_Freezes()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, withBarToday: false); // no asOf bar → the stoppage

            var outcome = Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var frozen = new LedgerStore(db).GetPosition(account, AaplId)!;
            Assert.True(frozen.Frozen);
            Assert.Contains("no bar", frozen.FrozenReason);
            Assert.Contains(outcome.Frozen, f => f.Id == AaplId);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>A dividend does NOT explain a missing bar (the name should still be trading), so a
    /// held name paying a dividend with no bar still freezes.</summary>
    [Fact]
    public void FR9_ADividendDoesNotExplainAMissingBar_StillFreezes()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, withBarToday: false);
            new CorporateActionIngestion(db).IngestDividends(Aapl,
                [new DividendEvent(AsOf, 0.24m, 0.25m)], ObservedEarly);

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            // The dividend still credits, AND the position freezes (the missing bar is unexplained).
            var store = new LedgerStore(db);
            Assert.Single(store.GetCashEvents(account, RunKind.Live), e => e.Type == CashEventType.Dividend);
            Assert.True(store.GetPosition(account, AaplId)!.Frozen);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR9_APricedName_IsNotFrozen()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, withBarToday: true); // has a bar today

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            Assert.False(new LedgerStore(db).GetPosition(account, AaplId)!.Frozen);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>The closed set of action types is enforced at the DB (the `ck_corporate_actions_type`
    /// CHECK, finding 121), so an unknown kind can never even be stored — a stronger guarantee than the
    /// applier's parse. This is why the applier's own `ParseType` fail-closed is defence-in-depth: it
    /// only becomes reachable if the CHECK is extended by migration without updating the map.</summary>
    [Fact]
    public void FR9_TheDbRejectsAnUnknownActionType_AtTheCheckConstraint()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            Seed(db);
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO corporate_actions (security_id, type, effective_date, version, observed_at, source) " +
                $"VALUES ({Aapl}, 'rights_offering', '{AsOf}', 1, '{ObservedEarly}', 'eodhd');";

            var ex = Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => { cmd.ExecuteNonQuery(); });
            Assert.Contains("ck_corporate_actions_type", ex.Message);
        }
        finally { TestDb.Delete(path); }
    }

    // ============================ Part 2 — mergers, spin-off, delist ============================

    private const long Acq = 2;
    private static readonly SecurityId AcqId = new(Acq);

    /// <summary>Register a second security (the acquirer / spun-off entity) so counterparty FKs resolve.</summary>
    private static void SeedAcquirer(AlphaLabDbContext db) => new SecurityMaster(db).Register("ACQ", "US", "2020-01-01");

    private static void AddAction(AlphaLabDbContext db, string type, decimal? cash = null, double? ratio = null,
        long? counterparty = null) =>
        db.CorporateActions.Add(new CorporateActionRow
        {
            SecurityId = Aapl, Type = type, EffectiveDate = AsOf, Version = 1,
            CashPerShare = cash, Ratio = ratio, CounterpartySecurityId = counterparty,
            ObservedAt = ObservedEarly, Source = "eodhd",
        });

    /// <summary>FX-MergerCash: held target, $54.20/share effective → position closed at deal cash, costs
    /// waived, action_id stamped, a Sell trade recorded, the position removed.</summary>
    [Fact]
    public void FR9_FxMergerCash_ClosesAtDealCash_CostsWaived_PositionRemoved()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 4_000m);
            AddAction(db, "merger_cash", cash: 54.20m);
            db.SaveChanges();

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            Assert.Null(store.GetPosition(account, AaplId));              // removed
            var sell = Assert.Single(store.GetTrades(account, RunKind.Live));
            Assert.Equal(TradeReason.CorpAction, sell.Reason);
            Assert.Equal(54.20m, sell.RawFillPrice);
            Assert.Equal(0m, sell.TotalCost);                            // waived
            Assert.NotNull(sell.ActionId);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>FX-MergerStock: 0.85 exchange ratio → shares converted across security_ids, basis carried.</summary>
    [Fact]
    public void FR9_FxMergerStock_ConvertsAcrossSecurityIds_BasisCarried()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 4_000m);
            SeedAcquirer(db);
            AddAction(db, "merger_stock", ratio: 0.85, counterparty: Acq);
            db.SaveChanges();

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            Assert.Null(store.GetPosition(account, AaplId));              // target gone
            var acquirer = store.GetPosition(account, AcqId)!;
            Assert.Equal(85, acquirer.Shares);                           // 100 × 0.85
            Assert.Equal(4_000m, acquirer.CostBasis);                    // basis carried
            Assert.Empty(store.GetTrades(account, RunKind.Live));         // a conversion is not a trade
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>FX-MergerMixed: $10 cash + 0.5 shares → both legs, one action.</summary>
    [Fact]
    public void FR9_FxMergerMixed_CreditsCashAndConvertsStock_OneAction()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 4_000m);
            SeedAcquirer(db);
            AddAction(db, "merger_mixed", cash: 10m, ratio: 0.5, counterparty: Acq);
            db.SaveChanges();

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            Assert.Null(store.GetPosition(account, AaplId));
            Assert.Equal(50, store.GetPosition(account, AcqId)!.Shares);  // 100 × 0.5
            var cash = Assert.Single(store.GetCashEvents(account, RunKind.Live), e => e.Type == CashEventType.MergerCash);
            Assert.Equal(1_000m, cash.Amount);                           // 100 × 10
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>FX-Spinoff (ratio in feed): a new position with basis allocation; the parent keeps its
    /// shares and its basis is reduced by exactly what moved. Basis conserved.</summary>
    [Fact]
    public void FR9_FxSpinoff_RatioInFeed_CreatesReceipt_ConservesBasis()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 5_000m);
            SeedAcquirer(db);
            AddAction(db, "spinoff", ratio: 0.25, counterparty: Acq); // 0.25/1.25 = 20% of basis
            db.SaveChanges();

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            var parent = store.GetPosition(account, AaplId)!;
            var spin = store.GetPosition(account, AcqId)!;
            Assert.Equal(100, parent.Shares);                            // parent keeps shares
            Assert.Equal(25, spin.Shares);                               // 100 × 0.25
            Assert.Equal(1_000m, spin.CostBasis);                        // 20% of 5,000
            Assert.Equal(5_000m, parent.CostBasis + spin.CostBasis);      // conserved
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>FX-Spinoff (ratio MISSING): the first-print allocation path. Needs the parent's and the
    /// spin-off's asOf prints; basis is split by relative value.</summary>
    [Fact]
    public void FR9_FxSpinoff_MissingRatio_UsesFirstPrintAllocation()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 5_000m); // AAPL asOf bar close = 150 (from Seed)
            SeedAcquirer(db);
            // The spun-off entity needs an asOf first print for the value-based split.
            new BarIngestionService(db).IngestEod(Acq,
                [new EodBar(AsOf, 15.0, 16.0, 14.0, 15.0, 15.0, 500_000)], ObservedEarly);
            AddAction(db, "spinoff", ratio: null, counterparty: Acq); // no ratio → first-print path
            db.SaveChanges();

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            var spin = store.GetPosition(account, AcqId)!;
            Assert.Equal(100, spin.Shares);                              // 1:1 fallback
            // parent value = 100×150 = 15,000; spinoff value = 100×15 = 1,500; fraction = 1,500/16,500 ≈ 0.0909.
            Assert.Equal(454.55m, spin.CostBasis);                       // 5,000 × 0.0909, rounded to cents
            Assert.Equal(5_000m, store.GetPosition(account, AaplId)!.CostBasis + spin.CostBasis);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>FX-Delist: force-exit at the last print (the asOf bar close), costs waived. Default
    /// haircut 0 → exit at the last print exactly.</summary>
    [Fact]
    public void FR9_FxDelist_ForceExitsAtLastPrint_CostsWaived()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 4_000m); // asOf bar close = 150
            AddAction(db, "delist");
            db.SaveChanges();

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var store = new LedgerStore(db);
            Assert.Null(store.GetPosition(account, AaplId));              // force-exited
            var sell = Assert.Single(store.GetTrades(account, RunKind.Live));
            Assert.Equal(150.0m, sell.RawFillPrice);                     // the last print
            Assert.Equal(0m, sell.TotalCost);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>FX-Delist bankruptcy variant: an 80%% haircut (config) exits at 20%% of the last print.</summary>
    [Fact]
    public void FR9_FxDelist_BankruptcyVariant_AppliesTheHaircutConfig()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 4_000m); // asOf bar close = 150
            AddAction(db, "delist");
            db.SaveChanges();

            var options = new CorporateActionsOptions { BankruptcyHaircutPct = 80.0 };
            Applier(db, options).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var sell = Assert.Single(new LedgerStore(db).GetTrades(account, RunKind.Live));
            Assert.Equal(30.0m, sell.RawFillPrice);                      // 150 × (1 − 0.80)
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>D74 PROOF: an index-membership drop is NOT a delisting. A held name that left the index
    /// but keeps printing bars, with no delist action, is neither force-exited nor frozen — the position
    /// survives untouched. (Membership is stamped on index_membership, never as a corporate action.)</summary>
    [Fact]
    public void FR9_D74_AnIndexDrop_IsNotADelist_ThePositionSurvivesUntouched()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db, shares: 100, basis: 4_000m, withBarToday: true); // still trading
            // No delist action exists — a membership drop would only stamp index_membership.removed_on.

            Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark);

            var position = new LedgerStore(db).GetPosition(account, AaplId)!;
            Assert.Equal(100, position.Shares);                          // untouched
            Assert.False(position.Frozen);                               // still printing → not frozen
            Assert.Empty(new LedgerStore(db).GetTrades(account, RunKind.Live)); // never force-exited
        }
        finally { TestDb.Delete(path); }
    }
}
