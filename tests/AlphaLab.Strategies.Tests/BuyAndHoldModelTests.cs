using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Strategies.Tests;

/// <summary>
/// The Buy&amp;Hold benchmarks (STRATEGY_CATALOG §5.1) — the model's scoring, and the acceptance bar:
/// enters once, never churns, and total return = the proxy's total return (price + dividends) minus one
/// entry cost, over a span that includes a dividend. The acceptance harness composes the real pure
/// engines (funnel → fill → §13.6 dividend → mark-to-market) exactly as the D53 pipeline (2.10) will,
/// but in memory — it is proving the STRATEGY, not the pipeline.
/// </summary>
public class BuyAndHoldModelTests
{
    private const string Wm = "2024-12-31T00:00:00Z";

    // ---- model shape + scoring ----
    [Fact]
    public void CapWeight_And_EqualWeight_HaveTheRightShapes()
    {
        var cw = BuyAndHoldModel.CapWeight();
        Assert.Equal("buyhold:cw", cw.Id);
        Assert.IsType<ExitPolicy.Never>(cw.Exits);
        Assert.Equal(SizingMode.Equal, cw.Config.Sizing);
        Assert.False(cw.Config.Unregistered); // a permanent baseline, not an unregistered candidate

        var ew = BuyAndHoldModel.EqualWeight();
        Assert.Equal("buyhold:ew", ew.Id);
        var rebalance = Assert.IsType<ExitPolicy.ScheduledRebalance>(ew.Exits);
        Assert.Equal(21, rebalance.EveryNDays); // ≈ monthly (D68)
    }

    [Fact]
    public async Task ScoreUniverse_ScoresEveryEligibleNameAtOne()
    {
        var model = BuyAndHoldModel.EqualWeight();
        var eligible = new[] { new SecurityId(7), new SecurityId(3), new SecurityId(42) };
        // Buy&Hold reads nothing, so an empty feature view is fine.
        var features = new FakeMarket().At(new DateOnly(2024, 1, 3), Wm);

        var scores = await model.ScoreUniverseAsync(eligible, new DateOnly(2024, 1, 3), features);

        Assert.Equal(3, scores.Count);
        Assert.All(scores.Values, v => Assert.Equal(1.0, v));
    }

    // =====================================================================================
    // §5.1 acceptance: cap-weight Buy&Hold over a span with a dividend.
    // =====================================================================================
    [Fact]
    public async Task Acceptance_CapWeight_EntersOnce_NeverChurns_TotalReturnEqualsProxyMinusOneEntryCost()
    {
        // ---- The fixture proxy (security_id 1). No overnight gaps on the entry (open[t]=close[t-1]) so
        // the shares sized off close[T] fill for exactly the target notional and §5.1's equality is EXACT
        // (OrderBuilder's fractional-shares note). Flat through the entry warm-up, then a ramp to 110. ----
        const long proxy = 1;
        var market = new FakeMarket();
        var start = new DateOnly(2024, 1, 1);
        string D(int t) => start.AddDays(t).ToString("yyyy-MM-dd");

        double Close(int t) => t <= 6 ? 100.0 : 100.0 + (t - 6) * 1.25; // close[6]=100 … close[14]=110
        for (var t = 0; t <= 14; t++)
        {
            var open = t == 0 ? 100.0 : Close(t - 1);   // no overnight gap
            market.Add(proxy, D(t), rawOpen: open, rawClose: Close(t), adjClose: Close(t), volume: 10_000_000);
        }

        const decimal startingCash = 100_000m;
        const int inception = 5;      // the account starts trading at session 5 (warm-up gives σ at the fill)
        const int exDay = 10;         // dividend ex-date
        const decimal divPerShare = 1.50m;

        var dividend = new CorporateAction
        {
            ActionId = 1, SecurityId = new SecurityId(proxy), Type = CorporateActionType.Dividend,
            ExDate = D(exDay), EffectiveDate = D(exDay), CashPerShare = divPerShare,
        };

        var model = BuyAndHoldModel.CapWeight();
        var broker = new VirtualBroker(new CostModel(new CostsOptions()));
        var guardrails = new GuardrailsOptions();                      // MaxConcurrentPositions 60 ≥ 1 name
        var sizing = new SizingOptions { PositionCapPct = 1.0 };       // a benchmark holds 100% in its one name
        const long accountId = 1;

        // ---- In-memory ledger state, driven day by day like the 2.10 pipeline. ----
        var cash = startingCash;
        Position? pos = null;
        var trades = new List<Trade>();
        IReadOnlyList<PlannedOrder> pending = [];

        for (var t = inception; t <= 14; t++)
        {
            var asOf = start.AddDays(t);
            var features = market.At(asOf, Wm);

            // 1) Corporate actions effective today, BEFORE the funnel/fills (D53 order): the dividend.
            if (t == exDay && pos is not null)
            {
                var effect = CorporateActionLedger.Apply(pos, dividend, RunKind.Live);
                var credited = Assert.IsType<CorporateActionEffect.DividendCredited>(effect);
                Assert.Equal((decimal)pos.Shares * divPerShare, credited.Cash.Amount);
                cash += credited.Cash.Amount;
            }

            // 2) Fill yesterday's orders at today's open (the T+1 half of decide-at-close-T).
            foreach (var order in pending)
            {
                var mkt = new MarketInputs
                {
                    RawPrice = (decimal)features.RawOpen(order.SecurityId, asOf)!.Value,
                    Adv21Shares = features.Adv21Shares(order.SecurityId),
                    Adv21Notional = features.Adv21Notional(order.SecurityId),
                    SigmaDaily = features.RealizedVolDaily(order.SecurityId, 21),
                };
                var fill = OrderFill.Fill(order, mkt, accountId, broker, RunKind.Live);
                var filled = Assert.IsType<FillResult.Filled>(fill);
                var trade = filled.Trade;
                trades.Add(trade);
                cash += trade.CashDelta; // buy: −(price×shares) − costs
                var addBasis = trade.RawFillPrice * (decimal)trade.Shares;
                pos = new Position
                {
                    AccountId = accountId,
                    SecurityId = order.SecurityId,
                    Shares = (pos?.Shares ?? 0) + trade.Shares,
                    CostBasis = (pos?.CostBasis ?? 0m) + addBasis,
                    OpenedOn = trade.FilledOn,
                };
            }
            pending = [];

            // 3) Decide today's orders (except on the last session — nothing to fill them the next day).
            if (t < 14)
            {
                var held = pos is null ? Array.Empty<Position>() : [pos];
                var equity = cash + (pos is null ? 0m : (decimal)pos.Shares * (decimal)features.RawClose(pos.SecurityId, asOf)!.Value);
                var outcome = await FunnelRunner.RunAsync(model, features, new FunnelInputs
                {
                    IndexMembers = [new SecurityId(proxy)], // the CW account's universe is the single proxy
                    Held = held,
                    Equity = equity,
                    Cash = cash, // D84: the account's real cash on hand (full at inception, ~0 once invested)
                    FillOn = start.AddDays(t + 1),
                    SessionsSinceInception = t - inception,
                }, guardrails, sizing);
                pending = outcome.Orders;
            }
        }

        // ---- Enters once, never churns: exactly one trade over the whole span. ----
        var entry = Assert.Single(trades);
        Assert.Equal(TradeSide.Buy, entry.Side);
        Assert.NotNull(pos);

        // ---- Total return = proxy total return − one entry cost (EXACT, no tolerance). ----
        var entryCost = entry.TotalCost;
        Assert.True(entryCost > 0m); // costs are always on (a mega-cap ETF still crosses the spread)

        var finalEquity = cash + (decimal)pos!.Shares * (decimal)Close(14);
        var accountTotalReturn = finalEquity / startingCash - 1m;

        // The proxy held from the fill open (=100), one dividend, to the final close (110).
        var proxyTotalReturn = ((decimal)Close(14) + divPerShare) / (decimal)market.Get(proxy, D(6))!.RawOpen - 1m;

        Assert.Equal(proxyTotalReturn - entryCost / startingCash, accountTotalReturn);
    }
}
