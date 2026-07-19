namespace AlphaLab.Core.Ledger;

/// <summary>
/// Cost-basis arithmetic for a position's ledger, kept PURE and in <c>decimal</c> end to end (D69).
///
/// FINDING 195: the sell-leg basis reduction previously scaled a decimal basis by a <c>double</c> ratio
/// (<c>costBasis * (decimal)(newShares / oldShares)</c>) — routing ledger money through binary floating
/// point against D69. The ratio is computed in <c>decimal</c> here instead. Shares stay <c>double</c>
/// (fractional shares are load-bearing — OrderBuilder / §13.8); only the MONEY math is decimal.
/// </summary>
public static class BasisMath
{
    /// <summary>The cost basis after buying <paramref name="buyShares"/> at <paramref name="rawFillPrice"/>
    /// on top of an existing <paramref name="existingBasis"/>: basis accrues at the RAW fill price (D30).</summary>
    public static decimal AddBuy(decimal existingBasis, decimal rawFillPrice, double buyShares) =>
        existingBasis + rawFillPrice * (decimal)buyShares;

    /// <summary>The cost basis remaining after a partial sell that leaves <paramref name="newShares"/> of
    /// an original <paramref name="oldShares"/>: reduced PROPORTIONALLY, in decimal — never a double ratio
    /// (D69, finding 195). The caller guarantees a partial sell (<c>0 &lt; newShares &lt; oldShares</c>);
    /// a full close removes the position row instead of reducing its basis.</summary>
    public static decimal ReduceForSale(decimal existingBasis, double newShares, double oldShares) =>
        existingBasis * (decimal)newShares / (decimal)oldShares;
}
