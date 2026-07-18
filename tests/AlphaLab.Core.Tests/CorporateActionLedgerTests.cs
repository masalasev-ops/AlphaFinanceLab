using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The pure §13.6 part-1 engine: dividend, split, ticker change, and the fail-closed stoppage
/// freeze. Arithmetic over a Position, so every case is a fixture pointed straight at the function.
/// </summary>
public class CorporateActionLedgerTests
{
    private static SecurityId S(long id) => new(id);

    private static Position Held(long id = 1, double shares = 100, decimal basis = 10_000m, bool frozen = false) => new()
    {
        AccountId = 7,
        SecurityId = S(id),
        Shares = shares,
        CostBasis = basis,
        OpenedOn = "2026-01-02",
        Frozen = frozen,
        FrozenReason = frozen ? "prior" : null,
    };

    private static CorporateAction Action(CorporateActionType type, long id = 1,
        decimal? cash = null, double? ratio = null, string? exDate = null, string effective = "2026-07-16",
        string? newSymbol = null) => new()
    {
        ActionId = 500,
        SecurityId = S(id),
        Type = type,
        ExDate = exDate,
        EffectiveDate = effective,
        CashPerShare = cash,
        Ratio = ratio,
        NewSymbol = newSymbol,
    };

    // ============================ Dividend ============================

    [Fact]
    public void FR9_Dividend_CreditsCashOnExDate_LeavingTheShareCountAlone()
    {
        var position = Held(shares: 100);
        var action = Action(CorporateActionType.Dividend, cash: 0.25m, exDate: "2026-07-16");

        var effect = CorporateActionLedger.Apply(position, action, RunKind.Live);

        var div = Assert.IsType<CorporateActionEffect.DividendCredited>(effect);
        Assert.Equal(25.00m, div.Cash.Amount);                 // 100 × 0.25
        Assert.Equal(CashEventType.Dividend, div.Cash.Type);
        Assert.Equal("2026-07-16", div.Cash.AsOf);             // ex-date, not pay date (D30)
        Assert.Equal(500, div.Cash.ActionId);                  // the action that produced it
        Assert.Equal(S(1), div.Cash.SecurityId);
    }

    /// <summary>Fractional shares (D68/§5.1) produce an exact decimal credit — no rounding, because
    /// B&H's total-return acceptance is exact.</summary>
    [Fact]
    public void FR9_Dividend_OnFractionalShares_CreditsAnExactDecimalAmount()
    {
        var effect = CorporateActionLedger.Apply(
            Held(shares: 1_000.5), Action(CorporateActionType.Dividend, cash: 0.37m, exDate: "2026-07-16"), RunKind.Live);

        var div = Assert.IsType<CorporateActionEffect.DividendCredited>(effect);
        Assert.Equal(370.185m, div.Cash.Amount);               // 1000.5 × 0.37, exact
    }

    [Fact]
    public void FR9_Dividend_WithNoCashAmount_FailsClosed_NeverCreditsZero()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CorporateActionLedger.Apply(Held(), Action(CorporateActionType.Dividend, cash: null), RunKind.Live));

        Assert.Contains("no cash-per-share", ex.Message);
    }

    [Fact]
    public void FR9_Dividend_WithNegativeCash_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(
            () => CorporateActionLedger.Apply(Held(), Action(CorporateActionType.Dividend, cash: -0.10m, exDate: "2026-07-16"), RunKind.Live));
    }

    // ============================ Split ============================

    /// <summary>
    /// A 2:1 split (ratio 2): shares double, TOTAL basis unchanged, equity unchanged. The last one
    /// is the invariant — equity before = shares × price = (shares×2) × (price÷2) = equity after —
    /// and it is why a split is neither a trade nor a cash event.
    /// </summary>
    [Fact]
    public void FR9_Split_MultipliesShares_KeepsTotalBasis_EquityUnchanged()
    {
        var before = Held(shares: 100, basis: 10_000m);
        var effect = CorporateActionLedger.Apply(before, Action(CorporateActionType.Split, ratio: 2.0), RunKind.Live);

        var restated = Assert.IsType<CorporateActionEffect.PositionRestated>(effect);
        Assert.Equal(200, restated.After.Shares);              // × ratio
        Assert.Equal(10_000m, restated.After.CostBasis);       // total basis unchanged
        Assert.Equal(before.OpenedOn, restated.After.OpenedOn);// not a new position

        // Equity invariance, made explicit: at a pre-split price of $150 the post-split quote is $75.
        const decimal preSplitPrice = 150m;
        var equityBefore = (decimal)before.Shares * preSplitPrice;
        var equityAfter = (decimal)restated.After.Shares * (preSplitPrice / 2m);
        Assert.Equal(equityBefore, equityAfter);
    }

    /// <summary>A reverse split (ratio 0.5): shares halve, basis still unchanged.</summary>
    [Fact]
    public void FR9_ReverseSplit_HalvesShares_KeepsTotalBasis()
    {
        var effect = CorporateActionLedger.Apply(
            Held(shares: 100, basis: 10_000m), Action(CorporateActionType.Split, ratio: 0.5), RunKind.Live);

        var restated = Assert.IsType<CorporateActionEffect.PositionRestated>(effect);
        Assert.Equal(50, restated.After.Shares);
        Assert.Equal(10_000m, restated.After.CostBasis);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void FR9_Split_WithNonPositiveOrNonFiniteRatio_FailsClosed(double ratio)
    {
        Assert.Throws<InvalidOperationException>(
            () => CorporateActionLedger.Apply(Held(), Action(CorporateActionType.Split, ratio: ratio), RunKind.Live));
    }

    // ============================ Ticker change (the D39 non-event) ============================

    /// <summary>
    /// The payoff of D39: a ticker change does NOTHING to the ledger. No trade, no cash, no position
    /// change — the identity is the security_id, and only a display alias moved. This is the classic
    /// symbol-keyed bug (rename read as delist-plus-new-listing) made impossible, and it is a tested
    /// fact rather than an assumption.
    /// </summary>
    [Fact]
    public void FR9_TickerChange_HasNoLedgerEffect_ZeroPhantomChurn()
    {
        var effect = CorporateActionLedger.Apply(
            Held(), Action(CorporateActionType.TickerChange, newSymbol: "META"), RunKind.Live);

        var noop = Assert.IsType<CorporateActionEffect.TickerRenamedNoLedgerEffect>(effect);
        Assert.Equal("META", noop.NewSymbol);
        Assert.Equal(S(1), noop.Id);
    }

    // ============================ Part 2 — mergers, spin-off, delist ============================

    private static SecurityId Acquirer => S(2);

    private static CorporateAction PartTwo(CorporateActionType type, decimal? cash = null, double? ratio = null,
        SecurityId? counterparty = null) => new()
    {
        ActionId = 600, SecurityId = S(1), Type = type, EffectiveDate = "2026-07-16",
        CashPerShare = cash, Ratio = ratio, CounterpartySecurityId = counterparty,
    };

    /// <summary>Cash merger: the whole position is force-CLOSED at the deal cash, with standard costs
    /// WAIVED (§13.6 — a corporate action, not a trade), the action_id stamped, and P&L realized.</summary>
    [Fact]
    public void FR9_CashMerger_ClosesAtDealCash_CostsWaived_ActionIdStamped()
    {
        var effect = CorporateActionLedger.Apply(
            Held(shares: 100, basis: 4_000m), PartTwo(CorporateActionType.MergerCash, cash: 54.20m), RunKind.Live);

        var sell = Assert.IsType<CorporateActionEffect.PositionForceClosed>(effect).Sell;
        Assert.Equal(TradeSide.Sell, sell.Side);
        Assert.Equal(100, sell.Shares);
        Assert.Equal(54.20m, sell.RawFillPrice);
        Assert.Equal(0m, sell.TotalCost);                    // costs waived
        Assert.Equal(TradeReason.CorpAction, sell.Reason);
        Assert.Equal(600, sell.ActionId);
        Assert.Equal(5_420m, sell.CashDelta);                // 100 × 54.20 released, no costs
    }

    /// <summary>Stock merger: shares convert at the exchange ratio into the acquirer's security_id and
    /// the cost basis CARRIES. No cash, no trade — a conversion realizes nothing.</summary>
    [Fact]
    public void FR9_StockMerger_ConvertsAtRatioIntoAcquirer_BasisCarries()
    {
        var effect = CorporateActionLedger.Apply(
            Held(shares: 100, basis: 4_000m), PartTwo(CorporateActionType.MergerStock, ratio: 0.85, counterparty: Acquirer),
            RunKind.Live);

        var converted = Assert.IsType<CorporateActionEffect.StockMergerConverted>(effect);
        Assert.Equal(Acquirer, converted.AcquirerAfter.SecurityId);
        Assert.Equal(85, converted.AcquirerAfter.Shares);    // 100 × 0.85
        Assert.Equal(4_000m, converted.AcquirerAfter.CostBasis); // basis carried across the security_id
    }

    /// <summary>Stock merger INTO A NAME ALREADY HELD: shares and basis SUM — you can be converted into
    /// an acquirer you already own, and the two lots merge.</summary>
    [Fact]
    public void FR9_StockMerger_IntoAnAlreadyHeldAcquirer_SumsSharesAndBasis()
    {
        var existing = new Position { AccountId = 7, SecurityId = Acquirer, Shares = 50, CostBasis = 3_000m, OpenedOn = "2025-01-02" };
        var ctx = new CorporateActionContext { ExistingCounterpartyPosition = existing };

        var effect = CorporateActionLedger.Apply(
            Held(shares: 100, basis: 4_000m), PartTwo(CorporateActionType.MergerStock, ratio: 0.85, counterparty: Acquirer),
            RunKind.Live, ctx);

        var acquirer = Assert.IsType<CorporateActionEffect.StockMergerConverted>(effect).AcquirerAfter;
        Assert.Equal(135, acquirer.Shares);                  // 50 + 85
        Assert.Equal(7_000m, acquirer.CostBasis);            // 3,000 + 4,000
    }

    /// <summary>Mixed merger: both legs in one action — cash credited AND stock converted.</summary>
    [Fact]
    public void FR9_MixedMerger_CreditsCash_AndConvertsStock_InOneAction()
    {
        var effect = CorporateActionLedger.Apply(
            Held(shares: 100, basis: 4_000m),
            PartTwo(CorporateActionType.MergerMixed, cash: 10m, ratio: 0.5, counterparty: Acquirer), RunKind.Live);

        var mixed = Assert.IsType<CorporateActionEffect.MixedMergerApplied>(effect);
        Assert.Equal(1_000m, mixed.Cash.Amount);             // 100 × 10 cash leg
        Assert.Equal(CashEventType.MergerCash, mixed.Cash.Type);
        Assert.Equal(50, mixed.AcquirerAfter.Shares);        // 100 × 0.5 stock leg
        Assert.Equal(4_000m, mixed.AcquirerAfter.CostBasis); // full basis carries to the stock leg
    }

    /// <summary>Delist: force-exit at the last print, costs waived. With a zero haircut the exit is at
    /// the last print exactly.</summary>
    [Fact]
    public void FR9_Delist_ForceExitsAtLastPrint_NoHaircut()
    {
        var ctx = new CorporateActionContext { LastPrintPrice = 3.50m, BankruptcyHaircut = 0.0 };

        var sell = Assert.IsType<CorporateActionEffect.PositionForceClosed>(
            CorporateActionLedger.Apply(Held(shares: 100, basis: 4_000m), PartTwo(CorporateActionType.Delist), RunKind.Live, ctx)).Sell;

        Assert.Equal(3.50m, sell.RawFillPrice);
        Assert.Equal(0m, sell.TotalCost);
        Assert.Equal(TradeReason.CorpAction, sell.Reason);
    }

    /// <summary>The bankruptcy variant: an 80% haircut exits at 20% of the last print.</summary>
    [Fact]
    public void FR9_Delist_BankruptcyHaircut_ReducesTheExitPrice()
    {
        var ctx = new CorporateActionContext { LastPrintPrice = 10m, BankruptcyHaircut = 0.80 };

        var sell = Assert.IsType<CorporateActionEffect.PositionForceClosed>(
            CorporateActionLedger.Apply(Held(shares: 100), PartTwo(CorporateActionType.Delist), RunKind.Live, ctx)).Sell;

        Assert.Equal(2.00m, sell.RawFillPrice);              // 10 × (1 − 0.80)
    }

    [Fact]
    public void FR9_Delist_WithoutAContext_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(
            () => CorporateActionLedger.Apply(Held(), PartTwo(CorporateActionType.Delist), RunKind.Live));
    }

    /// <summary>Spin-off: a NEW position is created and basis is CONSERVED — what the spin-off gains,
    /// the parent loses; the parent keeps its shares.</summary>
    [Fact]
    public void FR9_Spinoff_CreatesANewPosition_ConservingBasis()
    {
        var ctx = new CorporateActionContext { SpinoffShares = 20, SpinoffBasisAllocated = 1_500m };

        var effect = CorporateActionLedger.Apply(
            Held(shares: 100, basis: 4_000m), PartTwo(CorporateActionType.Spinoff, counterparty: Acquirer), RunKind.Live, ctx);

        var spin = Assert.IsType<CorporateActionEffect.SpinoffReceived>(effect);
        Assert.Equal(100, spin.ParentAfter.Shares);          // parent keeps its shares
        Assert.Equal(2_500m, spin.ParentAfter.CostBasis);    // 4,000 − 1,500
        Assert.Equal(Acquirer, spin.SpinoffPosition.SecurityId);
        Assert.Equal(20, spin.SpinoffPosition.Shares);
        Assert.Equal(1_500m, spin.SpinoffPosition.CostBasis);
        // Basis conserved to the cent.
        Assert.Equal(4_000m, spin.ParentAfter.CostBasis + spin.SpinoffPosition.CostBasis);
    }

    [Fact]
    public void FR9_Spinoff_AllocatingMoreBasisThanTheParentHas_FailsClosed()
    {
        var ctx = new CorporateActionContext { SpinoffShares = 20, SpinoffBasisAllocated = 5_000m };

        Assert.Throws<InvalidOperationException>(
            () => CorporateActionLedger.Apply(Held(basis: 4_000m), PartTwo(CorporateActionType.Spinoff, counterparty: Acquirer), RunKind.Live, ctx));
    }

    [Fact]
    public void FR9_ApplyingAnActionToTheWrongPosition_Throws()
    {
        // Action is for security 2, position is security 1 — a caller-side mismatch, caught loudly.
        var ex = Assert.Throws<ArgumentException>(
            () => CorporateActionLedger.Apply(Held(id: 1), Action(CorporateActionType.Dividend, id: 2, cash: 0.1m, exDate: "2026-07-16"), RunKind.Live));

        Assert.Contains("wrong position", ex.Message);
    }

    // ============================ Stoppage freeze (unmapped, fail closed) ============================

    [Fact]
    public void FR9_Stoppage_NoBarAndNoTerminalAction_Freezes()
    {
        var reason = CorporateActionLedger.StoppageFreezeReason(
            Held(), hasBarToday: false, hasTerminalActionToday: false, asOf: "2026-07-16");

        Assert.NotNull(reason);
        Assert.Contains("no bar", reason);
        Assert.Contains("fail closed", reason);
    }

    [Fact]
    public void FR9_Stoppage_BarPresent_DoesNotFreeze()
    {
        Assert.Null(CorporateActionLedger.StoppageFreezeReason(
            Held(), hasBarToday: true, hasTerminalActionToday: false, asOf: "2026-07-16"));
    }

    /// <summary>A terminal event (merger/spin-off/delist) EXPLAINS the missing bar — §13.6 part 2
    /// owns the exit, so the stoppage check must not also freeze it (which would fight the close).</summary>
    [Fact]
    public void FR9_Stoppage_NoBarButATerminalActionExplainsIt_DoesNotFreeze()
    {
        Assert.Null(CorporateActionLedger.StoppageFreezeReason(
            Held(), hasBarToday: false, hasTerminalActionToday: true, asOf: "2026-07-16"));
    }

    [Fact]
    public void FR9_Stoppage_AnAlreadyFrozenPosition_IsNotReFrozen()
    {
        Assert.Null(CorporateActionLedger.StoppageFreezeReason(
            Held(frozen: true), hasBarToday: false, hasTerminalActionToday: false, asOf: "2026-07-16"));
    }
}
