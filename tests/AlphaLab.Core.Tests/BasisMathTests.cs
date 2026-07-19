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
}
