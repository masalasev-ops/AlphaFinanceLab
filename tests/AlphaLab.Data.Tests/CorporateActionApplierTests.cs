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

    private static CorporateActionApplier Applier(AlphaLabDbContext db) => new(
        new LedgerStore(db),
        new CorporateActionReadService(db),
        new BarReadService(db));

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

    /// <summary>A dormant-feed action the ledger does not yet handle (a merger — §13.6 part 2, 2.7)
    /// makes the applier REFUSE loudly rather than silently skip it. The merger feeds are dormant at
    /// launch (D49), so this cannot occur on live data — but if one appeared, mispricing it silently is
    /// the failure this prevents.</summary>
    [Fact]
    public void FR9_ADormantMergerAction_MakesTheApplierRefuse_NotSilentlySkip()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var account = Seed(db);
            db.CorporateActions.Add(new CorporateActionRow
            {
                SecurityId = Aapl, Type = "merger_cash", EffectiveDate = AsOf, Version = 1,
                CashPerShare = 200m, ObservedAt = ObservedEarly, Source = "eodhd",
            });
            db.SaveChanges();

            var ex = Assert.Throws<NotSupportedException>(
                () => Applier(db).ApplyForAccount(account, RunKind.Live, AsOf, Watermark));
            Assert.Contains("2.7", ex.Message);
        }
        finally { TestDb.Delete(path); }
    }
}
