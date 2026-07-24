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

    /// <summary>The mark for ONE held position at a session's close (D86 / hard rule 10). Priced today ⇒ its
    /// raw close × shares. With NO bar today the two no-price cases are DISTINCT (finding 275 — the general
    /// marking bug): a <paramref name="frozen"/> position (unmapped CA / genuine bar-stoppage-without-event)
    /// marks at its <paramref name="costBasis"/> per D86 — a stale last print could misstate an unpriceable
    /// name in the one direction the honesty rails must never allow silently; but a NON-frozen position whose
    /// bar is merely MISSING (a vendor data gap on a session the name actually traded, e.g. OEF 2014-04-22)
    /// CARRIES FORWARD its <paramref name="lastKnownRawClose"/> × shares — its value has not changed as far as
    /// the lab knows, and jumping it to a years-old cost basis would fabricate a one-day round-trip in equity.
    /// Cost basis is the deep fallback only when the name was never priced at all ≤ today (should not occur for
    /// a held name). The OLD code applied cost basis to ANY missing bar, conflating the two.</summary>
    public static decimal MarkOne(double? rawCloseToday, bool frozen, double? lastKnownRawClose, double shares, decimal costBasis)
    {
        if (rawCloseToday is { } c) return (decimal)c * (decimal)shares;
        if (frozen) return costBasis;                                              // D86 — unpriceable frozen name
        if (lastKnownRawClose is { } last) return (decimal)last * (decimal)shares; // data gap ⇒ carry the last mark forward
        return costBasis;                                                          // never priced ≤ today (conservative)
    }
}
