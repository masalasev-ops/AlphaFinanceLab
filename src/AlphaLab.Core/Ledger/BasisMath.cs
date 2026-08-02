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

    /// <summary>The mark for ONE held position at a session's close (D119, amending D86 / hard rule 10).
    /// Priced today ⇒ its raw close × shares. No bar today ⇒ CARRY FORWARD <paramref name="lastKnownRawClose"/>
    /// × shares — frozen or not. Cost basis only when the name was never priced at all ≤ today.
    ///
    /// Finding 275 drew the frozen/gap distinction (frozen → cost basis per D86, plain gap → carry forward)
    /// but the stoppage freeze fires on the SAME session as the gap (§13.6 freezes on a single missing bar),
    /// so the carry-forward branch was unreachable for exactly the case it was written for: OEF 2014-04-22
    /// froze and marked at its 2006 cost basis, fabricating a −27 %/+37 % equity round-trip (finding 352).
    /// D119 resolves it: the last print is the lab's best point-in-time estimate and is the price the freeze
    /// reason already promises ("freezing at the last print"); a years-old cost basis carries NO information
    /// about today and misstates in EITHER direction. A freeze halts ACTION (no trading until an operator
    /// resolves), never VALUATION — which is why this signature no longer takes `frozen` at all: the mark
    /// must not depend on it, and removing the parameter makes that structural.</summary>
    public static decimal MarkOne(double? rawCloseToday, double? lastKnownRawClose, double shares, decimal costBasis)
    {
        if (rawCloseToday is { } c) return (decimal)c * (decimal)shares;
        if (lastKnownRawClose is { } last) return (decimal)last * (decimal)shares; // no bar today ⇒ last print (D119)
        return costBasis;                                                          // never priced ≤ today (conservative)
    }
}
