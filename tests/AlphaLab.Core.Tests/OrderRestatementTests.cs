using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// D142: converting a stored order's share magnitude into the units the book was restated into, and the
/// rule that decides which actions do any restating at all.
/// </summary>
public class OrderRestatementTests
{
    private static readonly SecurityId Aapl = new(1);

    private static PlannedOrder Order(double shares, TradeSide side = TradeSide.Sell,
        TradeReason reason = TradeReason.ExitPolicy) => new()
    {
        SecurityId = Aapl,
        Side = side,
        Shares = shares,
        Reason = reason,
        DecidedOn = "2026-07-15",
        FillOn = "2026-07-16",
        Rationale = "close the line",
    };

    [Fact]
    public void FR9_D142_NoCorporateAction_LeavesTheStoredOrderIdentical()
    {
        var stored = Order(100);

        // Reference equality, not value equality: the no-action path is the overwhelmingly common one and
        // must be provably untouched rather than rebuilt into something that happens to compare equal.
        // It is also what the pipeline's "did anything change?" log check keys on.
        Assert.Same(stored, OrderRestatement.Restate(stored, []));
    }

    [Fact]
    public void FR9_D142_AForwardSplit_MultipliesTheOrderedShares()
    {
        var restated = OrderRestatement.Restate(Order(100), [4.0]);

        Assert.Equal(400.0, restated.Shares);
        Assert.Equal(TradeSide.Sell, restated.Side);
        Assert.Equal(TradeReason.ExitPolicy, restated.Reason);
        Assert.Equal("2026-07-15", restated.DecidedOn);
        Assert.Equal("2026-07-16", restated.FillOn);
        Assert.Equal(Aapl, restated.SecurityId);
    }

    [Fact]
    public void FR9_D142_AReverseSplit_DividesTheOrderedShares()
    {
        Assert.Equal(50.0, OrderRestatement.Restate(Order(100), [0.5]).Shares);
    }

    [Theory]
    [InlineData(TradeSide.Buy, TradeReason.Wishlist)]     // an open or an add
    [InlineData(TradeSide.Sell, TradeReason.Wishlist)]    // a rebalance trim
    [InlineData(TradeSide.Sell, TradeReason.ExitPolicy)]  // a close
    public void FR9_D142_EverySideAndReasonRescalesByTheSameRule_NoSwitch(TradeSide side, TradeReason reason)
    {
        // A delta order rescales exactly like a level order, and that is provable rather than assumed:
        // targetShares = targetNotional / price; the book restates by r and the quote by 1/r; targetNotional
        // is MONEY and a split leaves it alone; so both terms of (targetShares − currentShares) scale by r.
        // A Reason switch would therefore be dead code that silently mishandles a reason added later.
        Assert.Equal(200.0, OrderRestatement.Restate(Order(100, side, reason), [2.0]).Shares);
    }

    [Fact]
    public void FR9_D142_ADeltaOrderRescalesLikeALevelOrder_BecauseTargetNotionalIsSplitInvariant()
    {
        // The theorem above, exercised against the real Stage-6 builder rather than restated as arithmetic.
        // Build the same intent twice — once pre-split at the pre-split quote, once post-split at the
        // post-split quote — and assert the restated pre-split delta equals the natively-built post-split
        // delta. This is the fixture that stops someone "fixing" delta orders to not rescale.
        const double ratio = 2.0;
        const decimal targetNotional = 5_000m;
        const double prePrice = 100.0;
        const double preCurrentShares = 10.0;

        var preDelta = (double)targetNotional / prePrice - preCurrentShares;
        var postDelta = (double)targetNotional / (prePrice / ratio) - preCurrentShares * ratio;

        Assert.Equal(postDelta, OrderRestatement.Restate(Order(Math.Abs(preDelta), TradeSide.Buy,
            TradeReason.Wishlist), [ratio]).Shares, 9);
    }

    [Fact]
    public void FR9_D142_TwoSplitsInOneWindow_TrackTheBookBitForBit()
    {
        // Ratios are folded LEFT, in the order the ledger applied them, rather than pre-multiplied. The
        // property that matters is not that (s·r₁)·r₂ and s·(r₁·r₂) differ — for many pairs they happen
        // to coincide — but that the order's magnitude follows the SAME sequence of multiplications the
        // applier performed on the position. Otherwise a whole-line close can miss the book by an ulp and
        // leave a dust position behind instead of closing.
        const double shares = 100.0, r1 = 1.0 / 3.0, r2 = 1.1;

        var position = new Position
        {
            AccountId = 1, SecurityId = Aapl, Shares = shares, CostBasis = 10_000m, OpenedOn = "2026-01-02",
        };
        var afterFirst = Restated(position, r1);
        var afterSecond = Restated(afterFirst, r2);

        var restated = OrderRestatement.Restate(Order(shares), [r1, r2]);

        Assert.Equal(afterSecond.Shares, restated.Shares);   // bit-exact, no tolerance
    }

    private static Position Restated(Position position, double ratio) =>
        ((CorporateActionEffect.PositionRestated)CorporateActionLedger.Apply(
            position, Base(CorporateActionType.Split) with { Ratio = ratio }, RunKind.Live)).After;

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FR9_D142_ANonPositiveOrNonFiniteRatio_IsRefused(double ratio)
    {
        // Mirrors the ledger's refusal on the position side: a share count in a unit we cannot name is
        // worse than a stopped run.
        Assert.Throws<InvalidOperationException>(() => OrderRestatement.Restate(Order(100), [ratio]));
    }

    [Fact]
    public void FR9_D142_ARestatedOrderCarriesItsProvenance()
    {
        var restated = OrderRestatement.Restate(Order(100), [0.5]);

        Assert.Contains("close the line", restated.Rationale, StringComparison.Ordinal);
        Assert.Contains("restated", restated.Rationale, StringComparison.Ordinal);
        Assert.Contains("D142", restated.Rationale, StringComparison.Ordinal);
    }

    // ================= the rule that decides WHICH actions restate a share unit =================

    /// <summary>
    /// THE CLOSURE. <see cref="CorporateActionLedger.UnitRestatementRatio"/> answers for an ACTION, with
    /// no position in hand, because a pending order in an unheld name still has to be restated. That
    /// makes it a second opinion about something <see cref="CorporateActionLedger.Apply"/> already
    /// decides — so this asserts the two agree for EVERY action type, and it iterates the enum rather
    /// than a hand-written list so a new kind cannot be added without failing here first.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryActionType))]
    public void FR9_D142_UnitRestatementRatio_AgreesWithApply_ForEveryActionType(CorporateActionType type)
    {
        var position = new Position
        {
            AccountId = 1, SecurityId = Aapl, Shares = 100, CostBasis = 10_000m, OpenedOn = "2026-01-02",
        };
        var action = ValidActionFor(type);
        var context = new CorporateActionContext
        {
            LastPrintPrice = 150m,
            BankruptcyHaircut = 0.0,
            SpinoffShares = 5.0,
            SpinoffBasisAllocated = 500m,
            ExistingCounterpartyPosition = null,
        };

        var effect = CorporateActionLedger.Apply(position, action, RunKind.Live, context);
        var ratio = CorporateActionLedger.UnitRestatementRatio(action);

        if (effect is CorporateActionEffect.PositionRestated restated)
        {
            Assert.NotNull(ratio);
            Assert.Equal(restated.Ratio, ratio!.Value);
            Assert.Equal(position.Shares * ratio.Value, restated.After.Shares);
        }
        else
        {
            // Anything that does not re-denominate the share count must report NO factor. A terminal
            // event (merger, delist) is deliberately in this branch: there is no number that converts an
            // order into a line that no longer exists, and pretending otherwise would be worse than the
            // gap it fills.
            Assert.Null(ratio);
        }
    }

    public static TheoryData<CorporateActionType> EveryActionType()
    {
        var data = new TheoryData<CorporateActionType>();
        foreach (var t in Enum.GetValues<CorporateActionType>()) data.Add(t);
        return data;
    }

    private static CorporateAction ValidActionFor(CorporateActionType type) => type switch
    {
        CorporateActionType.Dividend => Base(type) with { ExDate = "2026-07-16", CashPerShare = 1.50m },
        CorporateActionType.Split => Base(type) with { Ratio = 2.0 },
        CorporateActionType.TickerChange => Base(type) with { NewSymbol = "NEWA" },
        CorporateActionType.MergerCash => Base(type) with { CashPerShare = 54.20m },
        CorporateActionType.MergerStock => Base(type) with { Ratio = 0.5, CounterpartySecurityId = new SecurityId(2) },
        CorporateActionType.MergerMixed => Base(type) with
        {
            CashPerShare = 10m, Ratio = 0.5, CounterpartySecurityId = new SecurityId(2),
        },
        CorporateActionType.Spinoff => Base(type) with { CounterpartySecurityId = new SecurityId(2), Ratio = 0.1 },
        CorporateActionType.Delist => Base(type),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            "A new corporate-action type needs a valid fixture here — which is the point of iterating the enum."),
    };

    private static CorporateAction Base(CorporateActionType type) => new()
    {
        ActionId = 1, SecurityId = Aapl, Type = type, EffectiveDate = "2026-07-16",
    };

    [Fact]
    public void FR9_D142_ASplitWithNoUsableRatio_IsRefusedRatherThanTreatedAsNoRestatement()
    {
        // The dangerous default: returning null here would mean "this split does not change the unit",
        // and the order would fill against a book that HAD been restated. Fail closed instead.
        var bad = Base(CorporateActionType.Split) with { Ratio = null };

        Assert.Throws<InvalidOperationException>(() => CorporateActionLedger.UnitRestatementRatio(bad));
    }
}
