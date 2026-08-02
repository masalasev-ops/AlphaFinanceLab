using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// Cost-basis arithmetic (finding 195 / D69): the money math is decimal end to end, never a double
/// ratio. These pin the sell-leg reduction on inputs where the double ratio visibly drifts from the
/// exact decimal answer — the defect finding 195 records.
/// </summary>
public class BasisMathTests
{
    [Fact]
    public void AddBuy_AccruesBasisAtTheRawFillPrice_InDecimal()
    {
        Assert.Equal(1_010m, BasisMath.AddBuy(existingBasis: 0m, rawFillPrice: 10.10m, buyShares: 100));
        // A second lot compounds onto the running basis.
        Assert.Equal(1_515m, BasisMath.AddBuy(existingBasis: 1_010m, rawFillPrice: 10.10m, buyShares: 50));
    }

    /// <summary>Selling part of a line reduces the basis PROPORTIONALLY, in decimal. Thirds are the
    /// classic case where the double ratio 2/3 is inexact; the decimal answer is exact ($200).</summary>
    [Fact]
    public void ReduceForSale_ScalesTheBasisInDecimal_NotThroughADoubleRatio()
    {
        // Held 3 sh, basis $300 ($100/sh). Sell 1 ⇒ 2 remain ⇒ basis $200, exactly.
        Assert.Equal(200m, BasisMath.ReduceForSale(existingBasis: 300m, newShares: 2, oldShares: 3));

        // The finding-195 defect: scaling the decimal basis by a DOUBLE ratio drifts off the exact answer.
        var viaDoubleRatio = 300m * (decimal)(2.0 / 3.0);
        Assert.NotEqual(200m, viaDoubleRatio);
    }

    /// <summary>A partial sell leaves a strictly smaller basis, and the per-share basis is preserved
    /// (basis-per-share before == basis-per-share after) — the invariant a proportional reduction keeps.</summary>
    [Fact]
    public void ReduceForSale_PreservesBasisPerShare()
    {
        const decimal basis = 1_234.56m;
        const double oldShares = 78.0, newShares = 55.0;

        var reduced = BasisMath.ReduceForSale(basis, newShares, oldShares);

        Assert.True(reduced < basis);
        Assert.Equal(basis / (decimal)oldShares, reduced / (decimal)newShares, 20); // per-share basis unchanged
    }

    // D119 (finding 352, amending D86): with no bar today the position carries forward its last known
    // close — frozen or not. Finding 275's frozen→cost-basis branch was unreachable for the case it was
    // written for (the §13.6 stoppage freeze fires on the same session as the gap), so OEF 2014-04-22
    // froze and marked at its 2006 cost basis anyway — a fabricated −27 %/+37 % equity round-trip. A
    // freeze halts trading, never valuation; MarkOne no longer takes `frozen` so the mark CANNOT depend
    // on it.
    [Fact]
    public void MarkOne_PricedToday_UsesTodaysRawClose()
    {
        // 10 sh @ $85 today ⇒ $850, regardless of last-known / cost basis.
        Assert.Equal(850m, BasisMath.MarkOne(rawCloseToday: 85.0, lastKnownRawClose: 84.0, shares: 10, costBasis: 700m));
        Assert.Equal(850m, BasisMath.MarkOne(rawCloseToday: 85.0, lastKnownRawClose: null, shares: 10, costBasis: 700m));
    }

    [Fact]
    public void MarkOne_NoBarToday_CarriesForwardLastKnownClose_NotCostBasis_D119()
    {
        // The OEF-2014-04-22 case (finding 352): no bar today, name traded before (a last-known close
        // exists). It marks at 10 × $84 = $840 — the "last print" the freeze reason promises — NOT the
        // $700 cost basis, which fabricated a one-day crash-and-recover in the benchmark account.
        var mark = BasisMath.MarkOne(rawCloseToday: null, lastKnownRawClose: 84.0, shares: 10, costBasis: 700m);
        Assert.Equal(840m, mark);
        Assert.NotEqual(700m, mark);   // never the years-old cost basis when a last print exists
    }

    [Fact]
    public void MarkOne_NeverPriced_FallsBackToCostBasis()
    {
        // No bar today and NO prior bar at all ≤ today (should not happen for a held name) ⇒ the
        // conservative cost-basis fallback, never a fabricated price.
        Assert.Equal(700m, BasisMath.MarkOne(rawCloseToday: null, lastKnownRawClose: null, shares: 10, costBasis: 700m));
    }
}
