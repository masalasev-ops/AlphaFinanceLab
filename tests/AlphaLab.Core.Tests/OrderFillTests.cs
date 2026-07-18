using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The T+1 fill path: a stored Stage-6 order priced through the D43 broker into a Trade. Pins that
/// costs are always on, that the cap clips rather than rejects, that a missing input fails closed,
/// and that a forced-event order is refused here (it has its own §13.6 path).
/// </summary>
public class OrderFillTests
{
    private static SecurityId S(long id) => new(id);
    private static readonly VirtualBroker Broker = new(new CostModel(new CostsOptions()));

    private static PlannedOrder Order(TradeSide side, double shares, TradeReason reason = TradeReason.Wishlist) => new()
    {
        SecurityId = S(1),
        Side = side,
        Shares = shares,
        Reason = reason,
        DecidedOn = "2026-07-16",
        FillOn = "2026-07-17",
        Rationale = "test",
    };

    private static MarketInputs Market(double? price = 100.0, double? advShares = 1_000_000, double? advNotional = 1e8, double? sigma = 0.02) =>
        new() { RawPrice = (decimal?)price, Adv21Shares = advShares, Adv21Notional = advNotional, SigmaDaily = sigma };

    [Fact]
    public void FR9_AFilledOrder_BecomesATradeStampedWithTheCostModelVersion()
    {
        var result = OrderFill.Fill(Order(TradeSide.Buy, 100), Market(), accountId: 1, Broker, RunKind.Live);

        var filled = Assert.IsType<FillResult.Filled>(result);
        Assert.Equal(S(1), filled.Trade.SecurityId);
        Assert.Equal(100, filled.Trade.Shares);
        Assert.Equal("2026-07-16", filled.Trade.DecidedOn);
        Assert.Equal("2026-07-17", filled.Trade.FilledOn);
        Assert.Equal("cm-1.0", filled.Trade.CostModelVersion);
        Assert.Null(filled.Clip);
    }

    /// <summary>Costs are always on (rule 5): a fill's total cost is strictly positive even at the
    /// default zero commission, because the half-spread and impact are never waived on the funnel path.</summary>
    [Fact]
    public void FR10_AFill_AlwaysCarriesCost_TheHalfSpreadAndImpactAreNeverWaived()
    {
        var filled = Assert.IsType<FillResult.Filled>(
            OrderFill.Fill(Order(TradeSide.Buy, 100), Market(), 1, Broker, RunKind.Live));

        Assert.True(filled.Trade.TotalCost > 0m);
        Assert.Equal(0m, filled.Trade.Commission);   // default commission is zero...
        Assert.True(filled.Trade.SpreadCost > 0m);    // ...but the spread is not
    }

    /// <summary>The participation cap CLIPS: the order fills at the cap and the excess is surfaced as
    /// a CapacityClip (bound for capacity_rejections), never silently dropped.</summary>
    [Fact]
    public void FR10_AnOversizedOrder_FillsAtTheCap_AndSurfacesTheExcess()
    {
        // ADV 1,000 shares, 2% cap = 20 shares. Order 100 → fills 20, clips 80.
        var result = OrderFill.Fill(Order(TradeSide.Buy, 100), Market(advShares: 1_000), 1, Broker, RunKind.Live);

        var filled = Assert.IsType<FillResult.Filled>(result);
        Assert.Equal(20, filled.Trade.Shares);
        Assert.NotNull(filled.Clip);
        Assert.Equal(80, filled.Clip!.RejectedShares);
    }

    /// <summary>Rule 10: a missing risk input is a rejection with a reason, never a defaulted fill.</summary>
    [Fact]
    public void FR10_AMissingPrice_IsRejected_WithAReason()
    {
        var result = OrderFill.Fill(Order(TradeSide.Buy, 100), Market(price: null), 1, Broker, RunKind.Live);

        var rejected = Assert.IsType<FillResult.Rejected>(result);
        Assert.Equal(S(1), rejected.SecurityId);
        Assert.Contains("price", rejected.Reason);
    }

    [Fact]
    public void FR9_ASellReleasesCash_ABuyConsumesIt()
    {
        var buy = Assert.IsType<FillResult.Filled>(OrderFill.Fill(Order(TradeSide.Buy, 100), Market(), 1, Broker, RunKind.Live));
        var sell = Assert.IsType<FillResult.Filled>(OrderFill.Fill(Order(TradeSide.Sell, 100), Market(), 1, Broker, RunKind.Live));

        Assert.True(buy.Trade.CashDelta < 0m);   // buying spends
        Assert.True(sell.Trade.CashDelta > 0m);  // selling releases (minus costs)
    }

    /// <summary>A forced-event order (corp action / guardrail) is REFUSED here: it has its own §13.6
    /// priced path with costs waived and no cap, and routing it through the D43 broker would wrongly
    /// charge it spread + impact.</summary>
    [Theory]
    [InlineData(TradeReason.CorpAction)]
    [InlineData(TradeReason.Guardrail)]
    public void FR9_AForcedEventOrder_IsNotFilledHere(TradeReason reason)
    {
        // (A CorpAction order would also fail the ledger's action_id invariant; the point is it never
        // reaches the broker in the first place.)
        var order = Order(TradeSide.Sell, 100, reason);

        Assert.Throws<ArgumentException>(() => OrderFill.Fill(order, Market(), 1, Broker, RunKind.Live));
    }

    [Fact]
    public void FR9_TheRunKindStampsThroughToTheTrade()
    {
        var filled = Assert.IsType<FillResult.Filled>(
            OrderFill.Fill(Order(TradeSide.Buy, 100), Market(), 1, Broker, RunKind.Replay));

        Assert.Equal(RunKind.Replay, filled.Trade.RunKind);
    }
}
