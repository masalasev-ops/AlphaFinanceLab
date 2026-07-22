using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-9 — the ledger persistence seam (SCHEMA accounts/positions/trades/cash_events/equity_curve/
/// capacity_rejections). D69: every money value is decimal end-to-end, TEXT on disk.
/// </summary>
public class LedgerStoreTests
{
    private static readonly SecurityId Aapl = new(1);
    private static readonly SecurityId Msft = new(2);

    [Fact]
    public void FR9_OpenAccount_AssignsRowid_AndRecordsTheOpeningDeposit()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(
                new Account { StrategyId = "bh:cw", StartingCash = 100_000m }, "2026-01-02");

            Assert.True(account.AccountId > 0);   // plain INTEGER PRIMARY KEY still auto-assigns
            Assert.Equal(100_000m, account.StartingCash);

            // Equity/cash must reconcile from cash_events + trades alone — starting_cash cannot be
            // a number only the accounts row knows about.
            var deposit = Assert.Single(store.GetCashEvents(account.AccountId, RunKind.Live));
            Assert.Equal(CashEventType.Deposit, deposit.Type);
            Assert.Equal(100_000m, deposit.Amount);
            Assert.Null(deposit.SecurityId);
        });
    }

    [Fact]
    public void FR9_Money_RoundTripsExactly_AtCentAndSubCentScale()
    {
        // D69's whole point. 0.1 + 0.2 != 0.3 in binary float; a REAL ledger would drift a
        // fraction of a cent per fill and silently miscount alpha over thousands of trades.
        Run((db, store) =>
        {
            var account = store.OpenAccount(
                new Account { StrategyId = "s", StartingCash = 100_000.01m }, "2026-01-02");

            store.RecordTrade(NewTrade(account.AccountId, Aapl, TradeSide.Buy,
                shares: 3, price: 0.1m, commission: 0.1m, spread: 0.1m, impact: 0.1m));

            var trade = Assert.Single(store.GetTrades(account.AccountId, RunKind.Live));
            Assert.Equal(0.30m, trade.TotalCost);                       // exactly, not 0.30000000000000004
            Assert.Equal(100_000.01m, store.GetAccount(account.AccountId)!.StartingCash);
            Assert.Equal(-0.60m, trade.CashDelta);                      // 3 × 0.1 + 0.3 costs
        });
    }

    [Fact]
    public void FR9_UpsertPosition_ZeroShares_RemovesTheRow_RatherThanKeepingAGhostHolding()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");

            store.UpsertPosition(new Position
            {
                AccountId = account.AccountId, SecurityId = Aapl,
                Shares = 10, CostBasis = 1000m, OpenedOn = "2026-01-02",
            });
            Assert.Single(store.GetPositions(account.AccountId));

            // positions is CURRENT STATE, not a log. A zero-share row would render as a holding on
            // the Live screen; the trades log keeps the history.
            store.UpsertPosition(new Position
            {
                AccountId = account.AccountId, SecurityId = Aapl,
                Shares = 0, CostBasis = 0m, OpenedOn = "2026-01-02",
            });
            Assert.Empty(store.GetPositions(account.AccountId));
        });
    }

    [Fact]
    public void FR9_FreezePosition_RecordsTheReason_AndRefusesAPhantomPosition()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");
            store.UpsertPosition(new Position
            {
                AccountId = account.AccountId, SecurityId = Aapl,
                Shares = 10, CostBasis = 1000m, OpenedOn = "2026-01-02",
            });

            store.FreezePosition(account.AccountId, Aapl, "bars stopped 2026-03-01, no action in feed");

            var frozen = store.GetPosition(account.AccountId, Aapl)!;
            Assert.True(frozen.Frozen);
            Assert.Contains("no action in feed", frozen.FrozenReason);

            // An unexplained freeze is a mystery, not an alert — and freezing a position that
            // isn't held means the caller's view of the book is wrong. Fail closed.
            Assert.Throws<InvalidOperationException>(() => store.FreezePosition(account.AccountId, Msft, "x"));
        });
    }

    [Fact]
    public void FR9_CorpActionTrade_MustCarryItsActionId_AndOnlyACorpActionMay()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");

            // §13.6: every forced event is logged WITH its action id. A forced close whose cause
            // isn't recorded is unauditable.
            var orphan = NewTrade(account.AccountId, Aapl, TradeSide.Sell, reason: TradeReason.CorpAction);
            Assert.Throws<InvalidOperationException>(() => store.RecordTrade(orphan));

            // And the converse: an action_id on a signal trade is a miscategorized fill.
            var mislabelled = NewTrade(account.AccountId, Aapl, TradeSide.Buy,
                reason: TradeReason.Wishlist) with { ActionId = 99 };
            Assert.Throws<InvalidOperationException>(() => store.RecordTrade(mislabelled));

            var ok = store.RecordTrade(NewTrade(account.AccountId, Aapl, TradeSide.Sell,
                reason: TradeReason.CorpAction) with { ActionId = 99 });
            Assert.Equal(99, ok.ActionId);
        });
    }

    [Fact]
    public void FR9_Trades_AreQuarantinedByRunKind()
    {
        // Hard rule 1 / D37: a forward view can never return a replay row. There is deliberately
        // no "read all kinds" overload — the filter is not the caller's to remember.
        Run((db, store) =>
        {
            var live = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");
            var replay = store.OpenAccount(
                new Account { StrategyId = "s", StartingCash = 1000m, RunKind = RunKind.Replay }, "2026-01-02");

            store.RecordTrade(NewTrade(live.AccountId, Aapl, TradeSide.Buy));
            store.RecordTrade(NewTrade(replay.AccountId, Aapl, TradeSide.Buy) with { RunKind = RunKind.Replay });

            Assert.Single(store.GetTrades(live.AccountId, RunKind.Live));
            Assert.Empty(store.GetTrades(live.AccountId, RunKind.Replay));
            Assert.Single(store.GetTrades(replay.AccountId, RunKind.Replay));
            Assert.Empty(store.GetTrades(replay.AccountId, RunKind.Live));

            Assert.Single(store.GetAccounts(RunKind.Live));
            Assert.Single(store.GetAccounts(RunKind.Replay));
        });
    }

    [Fact]
    public void FR7_EquityPoint_IsIdempotentPerDay_SoARecoveredDayDoesNotDuplicate()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");

            store.RecordEquityPoint(account.AccountId, "2026-01-02", 1000m, 1000m, RunKind.Live);
            store.RecordEquityPoint(account.AccountId, "2026-01-02", 1010.50m, 500m, RunKind.Live);

            var point = Assert.Single(store.GetEquityCurve(account.AccountId, RunKind.Live));
            Assert.Equal(1010.50m, point.Equity);   // overwritten, not duplicated (FR-7)
            Assert.Equal(500m, point.Cash);
        });
    }

    [Fact]
    public void FR19_ReplayEquity_CannotOverwriteTheForwardCurve()
    {
        // run_kind is IN equity_curve's PK, so the quarantine holds at the key level — a replay of
        // the same day writes a SEPARATE row rather than clobbering forward evidence.
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");

            store.RecordEquityPoint(account.AccountId, "2026-01-02", 1000m, 1000m, RunKind.Live);
            store.RecordEquityPoint(account.AccountId, "2026-01-02", 7777m, 7777m, RunKind.Replay);

            Assert.Equal(1000m, Assert.Single(store.GetEquityCurve(account.AccountId, RunKind.Live)).Equity);
            Assert.Equal(7777m, Assert.Single(store.GetEquityCurve(account.AccountId, RunKind.Replay)).Equity);
        });
    }

    [Fact]
    public void FR10_CapacityRejection_IsLogged_AndIdempotentPerDay()
    {
        // D43: excess quantity is rejected, logged, and surfaced. A rejection nobody can see is
        // not capacity awareness.
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");

            store.RecordCapacityRejection(account.AccountId, Aapl, "2026-01-02", 30_000, 20_000, 1_000_000);
            store.RecordCapacityRejection(account.AccountId, Aapl, "2026-01-02", 31_000, 20_000, 1_000_000);

            var row = Assert.Single(db.CapacityRejections.ToList());
            Assert.Equal(31_000, row.IntendedShares);
            Assert.Equal(20_000, row.AllowedShares);
            Assert.Equal(1_000_000, row.Adv21);
        });
    }

    [Fact]
    public void FR8_Decision_IsIdempotentPerDay_AndRoundTrips()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");

            store.RecordDecision(account.AccountId, "2026-01-02", """{"stage3":["1"]}""", RunKind.Live);
            store.RecordDecision(account.AccountId, "2026-01-02", """{"stage3":["1","2"]}""", RunKind.Live);

            Assert.Equal("""{"stage3":["1","2"]}""",
                store.GetDecisionJson(account.AccountId, "2026-01-02", RunKind.Live));
            Assert.Single(db.Decisions.ToList());
            Assert.Null(store.GetDecisionJson(account.AccountId, "2026-01-02", RunKind.Replay));
        });
    }

    [Fact]
    public void FR9_UnmappedRunKindToken_FailsClosed()
    {
        // A ledger row of unknown provenance cannot be classified forward-or-replay, and guessing
        // would breach the D37 quarantine.
        Assert.Throws<InvalidOperationException>(() => LedgerMapping.ParseRunKind("banana"));

        // 'catchup' is FORWARD evidence, not a third kind: nothing downstream may treat a
        // caught-up day as lesser evidence than a same-day one (hard rule 1).
        Assert.Equal(RunKind.Live, LedgerMapping.ParseRunKind("catchup"));
        Assert.Equal(RunKind.Live, LedgerMapping.ParseRunKind("live"));
        Assert.Equal(RunKind.Replay, LedgerMapping.ParseRunKind("replay"));
    }

    [Fact]
    public void FR9_TradeSideCheckConstraint_RejectsAnInvalidToken_AtTheDb()
    {
        // The one CHECK SCHEMA declares on the ledger tables. Prove it is really on disk, not just
        // in the model.
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "s", StartingCash = 1000m }, "2026-01-02");
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO trades (account_id, security_id, side, decided_on, filled_on, shares, " +
                "raw_fill_price, commission, spread_cost, impact_cost, cost_model_version, reason, run_kind) " +
                $"VALUES ({account.AccountId}, 1, 'hodl', '2026-01-02', '2026-01-05', 1, '1', '0', '0', '0', 'cm-1.0', 'wishlist', 'live');";

            var ex = Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => { cmd.ExecuteNonQuery(); });
            Assert.Contains("ck_trades_side", ex.Message);
        });
    }

    private static Trade NewTrade(
        long accountId, SecurityId securityId, TradeSide side,
        double shares = 10, decimal price = 100m, decimal commission = 0m,
        decimal spread = 0m, decimal impact = 0m, TradeReason reason = TradeReason.Wishlist) => new()
    {
        AccountId = accountId,
        SecurityId = securityId,
        Side = side,
        DecidedOn = "2026-01-02",
        FilledOn = "2026-01-05",
        Shares = shares,
        RawFillPrice = price,
        Commission = commission,
        SpreadCost = spread,
        ImpactCost = impact,
        CostModelVersion = "cm-1.0",
        Reason = reason,
    };

    // ---- position_snapshots: the end-of-day book (D90) ----

    [Fact]
    public void D90_PositionSnapshot_RoundTripsTheWholeBook_IncludingFrozenState()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "bh:cw", StartingCash = 100_000m }, "2026-01-02");
            var book = new List<Position>
            {
                new() { AccountId = account.AccountId, SecurityId = Aapl, Shares = 10, CostBasis = 1_234.56m, OpenedOn = "2026-01-02" },
                new()
                {
                    AccountId = account.AccountId, SecurityId = Msft, Shares = 3.5, CostBasis = 987.65m,
                    OpenedOn = "2025-12-01", Frozen = true, FrozenReason = "bar stoppage, no mapped action",
                },
            };

            store.RecordPositionSnapshot(account.AccountId, "2026-01-05", book, RunKind.Live);
            var restored = store.GetPositionSnapshot(account.AccountId, "2026-01-05", RunKind.Live);

            // Every field must survive: frozen/frozen_reason are rendered verbatim into the funnel's
            // stage_json, so a lossy snapshot would make a frozen day un-reproducible (FR-25).
            Assert.Equal(2, restored.Count);
            Assert.Equal(book[0], restored[0]);
            Assert.Equal(book[1], restored[1]);
            Assert.Equal(1_234.56m, restored[0].CostBasis);   // decimal → TEXT, exact (D69)
        });
    }

    [Fact]
    public void D90_PositionSnapshot_IsIdempotentPerDay_AndDropsNamesThatLeftTheBook()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "bh:cw", StartingCash = 100_000m }, "2026-01-02");
            store.RecordPositionSnapshot(account.AccountId, "2026-01-05",
            [
                new() { AccountId = account.AccountId, SecurityId = Aapl, Shares = 10, CostBasis = 100m, OpenedOn = "2026-01-02" },
                new() { AccountId = account.AccountId, SecurityId = Msft, Shares = 5, CostBasis = 50m, OpenedOn = "2026-01-02" },
            ], RunKind.Live);

            // A recovered day re-runs and closes MSFT. Re-recording must land on the same book (FR-7),
            // not leave the sold name behind as a phantom holding.
            store.RecordPositionSnapshot(account.AccountId, "2026-01-05",
            [
                new() { AccountId = account.AccountId, SecurityId = Aapl, Shares = 12, CostBasis = 120m, OpenedOn = "2026-01-02" },
            ], RunKind.Live);

            var restored = store.GetPositionSnapshot(account.AccountId, "2026-01-05", RunKind.Live);
            Assert.Single(restored);
            Assert.Equal(Aapl, restored[0].SecurityId);
            Assert.Equal(12, restored[0].Shares);
        });
    }

    [Fact]
    public void D90_PositionSnapshot_QuarantinesReplayFromForward()
    {
        Run((db, store) =>
        {
            var account = store.OpenAccount(new Account { StrategyId = "bh:cw", StartingCash = 100_000m }, "2026-01-02");
            store.RecordPositionSnapshot(account.AccountId, "2026-01-05",
                [new() { AccountId = account.AccountId, SecurityId = Aapl, Shares = 10, CostBasis = 100m, OpenedOn = "2026-01-02" }],
                RunKind.Live);
            store.RecordPositionSnapshot(account.AccountId, "2026-01-05",
                [new() { AccountId = account.AccountId, SecurityId = Msft, Shares = 99, CostBasis = 999m, OpenedOn = "2026-01-02" }],
                RunKind.Replay);

            // run_kind is IN the PK (the equity_curve precedent, D37): a replay book cannot overwrite
            // the forward one, and each reads back its own.
            Assert.Equal(Aapl, Assert.Single(store.GetPositionSnapshot(account.AccountId, "2026-01-05", RunKind.Live)).SecurityId);
            Assert.Equal(Msft, Assert.Single(store.GetPositionSnapshot(account.AccountId, "2026-01-05", RunKind.Replay)).SecurityId);
        });
    }

    [Fact]
    public void D90_PositionSnapshot_MissingDay_IsEmptyNotAnError()
    {
        Run((db, store) =>
        {
            // Inception (or any day before D90 shipped): an empty book, which is exactly what a
            // reproduction should start from — never a throw, never a silently-substituted later book.
            Assert.Empty(store.GetPositionSnapshot(1, "2026-01-05", RunKind.Live));
        });
    }

    private static void Run(Action<AlphaLabDbContext, ILedgerStore> body)
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            body(db, new LedgerStore(db));
        }
        finally { TestDb.Delete(path); }
    }
}
