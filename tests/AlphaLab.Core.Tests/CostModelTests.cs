using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// FX-CostModel (TEST_PLAN §3) — FR-10 / D43: "one order per liquidity bucket + one at 3% ADV;
/// spread bucket applied; impact = k·σ·√(Q/ADV) to 1e-9; participation excess rejected +
/// capacity_rejections row; cost_model_version stamped."
///
/// Whether a strategy survives net of costs is the single most consequential number in the lab,
/// so these assert the arithmetic itself, not just that "some cost was charged".
/// </summary>
public class CostModelTests
{
    private static readonly SecurityId Sec = new(1);

    private static CostsOptions Defaults() => new();   // mirrors CONFIG_REFERENCE
    private static CostModel Model() => new(Defaults());
    private static VirtualBroker Broker() => new(Model());

    // A deliberately round fixture so the expected numbers are checkable by hand.
    private const decimal Price = 100m;
    private const double Sigma = 0.02;          // 2%/day
    private const double AdvShares = 1_000_000; // cap = 2% = 20,000 shares

    private static MarketInputs Inputs(double advNotional = 5.0e8) => new()
    {
        RawPrice = Price,
        Adv21Shares = AdvShares,
        Adv21Notional = advNotional,
        SigmaDaily = Sigma,
    };

    // ---- One order per liquidity bucket ----

    [Theory]
    [InlineData(5.0e8, LiquidityBucket.Mega, 1.0)]    // ≥ $400M/day
    [InlineData(4.0e8, LiquidityBucket.Mega, 1.0)]    // threshold is an inclusive lower bound
    [InlineData(2.0e8, LiquidityBucket.Large, 2.5)]   // ≥ $100M/day
    [InlineData(1.0e8, LiquidityBucket.Large, 2.5)]   // inclusive
    [InlineData(9.9e7, LiquidityBucket.Other, 5.0)]   // just below → widest spread
    [InlineData(1.0e6, LiquidityBucket.Other, 5.0)]
    public void FR10_SpreadBucket_IsKeyedByAdvNotional(double advNotional, LiquidityBucket expected, double expectedBp)
    {
        var model = Model();

        Assert.Equal(expected, model.Bucket(advNotional));
        Assert.Equal(expectedBp, model.HalfSpreadBp(expected));

        // 1,000 shares × $100 = $100,000 notional; spread = notional × bp/10,000.
        var result = Assert.IsType<BrokerResult.Filled>(
            Broker().Execute(Sec, TradeSide.Buy, 1_000, Inputs(advNotional)));

        Assert.Equal(expected, result.Costs.Bucket);
        Assert.Equal(100_000m * (decimal)(expectedBp / 10_000.0), result.Costs.SpreadCost);
    }

    // ---- impact = k·σ·√(Q/ADV) to 1e-9 ----

    [Fact]
    public void FR10_Impact_MatchesTheSquareRootLaw_To1e9()
    {
        var model = Model();

        // Q = 10,000 of ADV 1,000,000 ⇒ Q/ADV = 0.01 ⇒ √ = 0.1.
        // fraction = k·σ·√(Q/ADV) = 0.1 × 0.02 × 0.1 = 0.0002
        const double expectedFraction = 0.1 * 0.02 * 0.1;
        Assert.Equal(expectedFraction, model.ImpactFraction(10_000, AdvShares, Sigma), 9);

        // cost = notional × fraction = (10,000 × $100) × 0.0002 = $200
        var result = Assert.IsType<BrokerResult.Filled>(
            Broker().Execute(Sec, TradeSide.Buy, 10_000, Inputs()));
        Assert.Equal(200.0, (double)result.Costs.ImpactCost, 9);
    }

    [Fact]
    public void FR10_Impact_IsSubLinear_SoDoublingSizeCostsLessThanDoubleTheImpact()
    {
        // The whole point of the square-root law. If this ever reads linear, the model has been
        // silently replaced with a proportional one and capacity stops biting.
        var model = Model();

        var small = model.ImpactFraction(10_000, AdvShares, Sigma);
        var doubled = model.ImpactFraction(20_000, AdvShares, Sigma);

        Assert.True(doubled < 2 * small);
        Assert.Equal(Math.Sqrt(2.0), doubled / small, 9);   // exactly √2, not 2
    }

    [Fact]
    public void FR10_Impact_IsZero_WhenTheNameHasNoVolatility_ButStillPaysSpread()
    {
        // σ = 0 is a legitimate (if degenerate) input, distinct from σ missing: a name that did
        // not move has no impact to model, but you still cross the spread.
        var result = Assert.IsType<BrokerResult.Filled>(
            Broker().Execute(Sec, TradeSide.Buy, 1_000, Inputs() with { SigmaDaily = 0 }));

        Assert.Equal(0m, result.Costs.ImpactCost);
        Assert.True(result.Costs.SpreadCost > 0);
    }

    // ---- One order at 3% ADV: the participation cap ----

    [Fact]
    public void FR10_ParticipationCap_RejectsAndLogs()
    {
        // 30,000 shares against ADV 1,000,000 = 3% — over the 2% cap. The EXCESS is rejected, not
        // the order: the fill happens at the cap and the shortfall is surfaced.
        var result = Assert.IsType<BrokerResult.Filled>(
            Broker().Execute(Sec, TradeSide.Buy, 30_000, Inputs()));

        Assert.Equal(20_000, result.Shares);            // 2% of ADV

        var clip = Assert.IsType<CapacityClip>(result.Clip);
        Assert.Equal(30_000, clip.IntendedShares);
        Assert.Equal(20_000, clip.AllowedShares);
        Assert.Equal(10_000, clip.RejectedShares);
        Assert.Equal(AdvShares, clip.Adv21Shares);      // capacity_rejections.adv21 is in SHARES
    }

    [Fact]
    public void FR10_CostsArePricedOnTheAllowedSize_NotTheIntendedOne()
    {
        // You do not pay impact on shares you never traded. Pricing the intended size would
        // overstate the cost of being capacity-constrained and double-punish illiquid names.
        var capped = Assert.IsType<BrokerResult.Filled>(Broker().Execute(Sec, TradeSide.Buy, 30_000, Inputs()));
        var exact = Assert.IsType<BrokerResult.Filled>(Broker().Execute(Sec, TradeSide.Buy, 20_000, Inputs()));

        Assert.Equal(exact.Costs.ImpactCost, capped.Costs.ImpactCost);
        Assert.Equal(exact.Costs.SpreadCost, capped.Costs.SpreadCost);
    }

    [Fact]
    public void FR10_UnderTheCap_FillsWhole_WithNoClipToLog()
    {
        var result = Assert.IsType<BrokerResult.Filled>(Broker().Execute(Sec, TradeSide.Buy, 19_999, Inputs()));

        Assert.Equal(19_999, result.Shares);
        Assert.Null(result.Clip);   // no capacity_rejections row for an order that fit
    }

    [Fact]
    public void FR10_CapAppliesToSellsToo_BecauseExitingIsAlsoCapacityConstrained()
    {
        // A book that can't exit is a real risk; capping only entries would model a fantasy.
        var result = Assert.IsType<BrokerResult.Filled>(Broker().Execute(Sec, TradeSide.Sell, 30_000, Inputs()));

        Assert.Equal(20_000, result.Shares);
        Assert.NotNull(result.Clip);
    }

    // ---- cost_model_version stamped ----

    [Fact]
    public void FR10_CostModelVersion_IsStampedOnEveryFill()
    {
        // D43: every fill stays attributable to the exact model that priced it.
        var result = Assert.IsType<BrokerResult.Filled>(Broker().Execute(Sec, TradeSide.Buy, 100, Inputs()));
        Assert.Equal("cm-1.0", result.Costs.CostModelVersion);

        // And it follows config, not a constant — a re-parameterized model gets a new version.
        var reparameterized = new VirtualBroker(new CostModel(new CostsOptions { ModelVersion = "cm-2.0" }));
        var v2 = Assert.IsType<BrokerResult.Filled>(reparameterized.Execute(Sec, TradeSide.Buy, 100, Inputs()));
        Assert.Equal("cm-2.0", v2.Costs.CostModelVersion);
    }

    [Fact]
    public void FR10_Commission_IsConfig_AndDefaultsToZero()
    {
        var free = Assert.IsType<BrokerResult.Filled>(Broker().Execute(Sec, TradeSide.Buy, 100, Inputs()));
        Assert.Equal(0m, free.Costs.Commission);

        var charged = new VirtualBroker(new CostModel(new CostsOptions { CommissionPerTrade = 1.25m }));
        var result = Assert.IsType<BrokerResult.Filled>(charged.Execute(Sec, TradeSide.Buy, 100, Inputs()));
        Assert.Equal(1.25m, result.Costs.Commission);
        Assert.Equal(result.Costs.Commission + result.Costs.SpreadCost + result.Costs.ImpactCost, result.Costs.Total);
    }

    // ---- F-CLOSED: a missing risk input rejects, never defaults ----

    public static TheoryData<string, MarketInputs, string> MissingInputs => new()
    {
        { "price", new MarketInputs { Adv21Shares = AdvShares, Adv21Notional = 5.0e8, SigmaDaily = Sigma }, "price" },
        { "adv shares", new MarketInputs { RawPrice = Price, Adv21Notional = 5.0e8, SigmaDaily = Sigma }, "ADV (shares)" },
        { "adv notional", new MarketInputs { RawPrice = Price, Adv21Shares = AdvShares, SigmaDaily = Sigma }, "ADV (notional)" },
        { "sigma", new MarketInputs { RawPrice = Price, Adv21Shares = AdvShares, Adv21Notional = 5.0e8 }, "volatility" },
    };

    [Theory]
    [MemberData(nameof(MissingInputs))]
    public void FR11_MissingRiskInput_RejectsWithALoggedReason_NeverDefaults(
        string _, MarketInputs inputs, string expectedInReason)
    {
        // F-CLOSED / hard rule 10. A missing ADV must not become "assume unlimited liquidity"; a
        // missing σ must not become "assume zero impact". Both would price the fill as free —
        // exactly the error the cost model exists to make impossible.
        var rejected = Assert.IsType<BrokerResult.Rejected>(Broker().Execute(Sec, TradeSide.Buy, 100, inputs));
        Assert.Contains(expectedInReason, rejected.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void FR11_NonsensicalOrderSize_IsRejected(double shares)
    {
        var rejected = Assert.IsType<BrokerResult.Rejected>(Broker().Execute(Sec, TradeSide.Buy, shares, Inputs()));
        Assert.NotEmpty(rejected.Reason);
    }

    [Fact]
    public void FR11_NonPositivePrice_IsRejected_NotTradedAtZero()
    {
        var rejected = Assert.IsType<BrokerResult.Rejected>(
            Broker().Execute(Sec, TradeSide.Buy, 100, Inputs() with { RawPrice = 0m }));
        Assert.Contains("price", rejected.Reason);
    }

    [Fact]
    public void FR10_CapAllowingNothing_Rejects_ButStillReportsTheCapacityFact()
    {
        // The only way the cap allows nothing while ADV is positive is a cap configured to 0% —
        // i.e. "never trade". That is a misconfiguration, not a market fact, so it must refuse
        // loudly rather than quietly fill nothing. Reached via config because ADV > 0 is already
        // guarded above: for any real ADV, 2% of it is positive.
        var zeroCap = new VirtualBroker(new CostModel(new CostsOptions { ParticipationCapPctAdv = 0 }));

        var rejected = Assert.IsType<BrokerResult.Rejected>(zeroCap.Execute(Sec, TradeSide.Buy, 100, Inputs()));

        Assert.Contains("participation cap", rejected.Reason);
        // "Too constrained to trade at all" is itself the capacity finding the operator needs, so
        // the clip rides along for the capacity_rejections row.
        Assert.NotNull(rejected.Clip);
        Assert.Equal(100, rejected.Clip!.IntendedShares);
        Assert.Equal(0, rejected.Clip.AllowedShares);
    }

    [Fact]
    public void FR10_AVanishinglyThinNameStillFillsProportionally_RatherThanInventingAMinimumSize()
    {
        // Deliberate non-behaviour. A tiny-but-positive ADV yields a tiny-but-positive cap, and
        // the model fills it. Introducing a "minimum tradeable size" floor here would mean
        // inventing a CONFIG key that CONFIG_REFERENCE does not define — and the lab's rule is
        // never to invent one. Real ADVs come from real volume and never look like this; if that
        // ever stops being true, it is a config decision (a D-number), not a quiet constant.
        var result = Assert.IsType<BrokerResult.Filled>(
            Broker().Execute(Sec, TradeSide.Buy, 100, Inputs() with { Adv21Shares = 0.0000001 }));

        Assert.Equal(0.0000001 * 0.02, result.Shares, 15);
        Assert.NotNull(result.Clip);
    }

    [Fact]
    public void FR10_CostsAreAlwaysOn_ThereIsNoCostFreeFlagOnThisPath()
    {
        // Hard rule 5. The cost-free control population (D36) is display-only and gets its own
        // construction; a flag here would let a cost-free number reach a forward verdict and
        // flatter every strategy. Asserted structurally so a future "convenience" flag reddens.
        var brokerMethods = typeof(VirtualBroker).GetMethods()
            .Where(m => m.DeclaringType == typeof(VirtualBroker));

        Assert.DoesNotContain(brokerMethods.SelectMany(m => m.GetParameters()),
            p => p.ParameterType == typeof(bool));
    }
}
