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

    // finding 275 — the general marking bug: with no bar today, a FROZEN name marks at cost basis (D86), but a
    // NON-frozen name whose bar is merely missing (a data gap) carries forward its last known close — NOT a
    // years-old cost basis, which would fabricate a one-day equity round-trip.
    [Fact]
    public void MarkOne_PricedToday_UsesTodaysRawClose()
    {
        // 10 sh @ $85 today ⇒ $850, regardless of frozen flag / last-known / cost basis.
        Assert.Equal(850m, BasisMath.MarkOne(rawCloseToday: 85.0, frozen: false, lastKnownRawClose: 84.0, shares: 10, costBasis: 700m));
        Assert.Equal(850m, BasisMath.MarkOne(rawCloseToday: 85.0, frozen: true, lastKnownRawClose: null, shares: 10, costBasis: 700m));
    }

    [Fact]
    public void MarkOne_FrozenNoBar_MarksAtCostBasis_D86()
    {
        // An unpriceable FROZEN name marks at cost basis even though a last-known close exists (D86: a stale
        // print could misstate an unpriceable name silently).
        Assert.Equal(700m, BasisMath.MarkOne(rawCloseToday: null, frozen: true, lastKnownRawClose: 84.0, shares: 10, costBasis: 700m));
    }

    [Fact]
    public void MarkOne_NonFrozenDataGap_CarriesForwardLastKnownClose_NotCostBasis()
    {
        // The OEF-2014-04-22 case: no bar today, NOT frozen, name traded (a last-known close exists). It marks
        // at 10 × $84 = $840 (carry-forward), NOT the $700 cost basis (which would crash-and-recover in a day).
        var mark = BasisMath.MarkOne(rawCloseToday: null, frozen: false, lastKnownRawClose: 84.0, shares: 10, costBasis: 700m);
        Assert.Equal(840m, mark);
        Assert.NotEqual(700m, mark);   // never the years-old cost basis for a plain data gap
    }

    [Fact]
    public void MarkOne_NeverPriced_FallsBackToCostBasis()
    {
        // No bar today, not frozen, and NO prior bar at all ≤ today (should not happen for a held name) ⇒ the
        // conservative cost-basis fallback, never a fabricated price.
        Assert.Equal(700m, BasisMath.MarkOne(rawCloseToday: null, frozen: false, lastKnownRawClose: null, shares: 10, costBasis: 700m));
    }
}
