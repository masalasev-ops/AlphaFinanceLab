namespace AlphaLab.Core.Ledger;

/// <summary>The resolved spin-off terms: how many shares of the spun-off entity the account receives,
/// and how much of the parent's cost basis moves with them. Basis is CONSERVED — what the spin-off
/// gains, the parent loses.</summary>
public sealed record SpinoffTerms(double SpinoffShares, decimal BasisToSpinoff);

/// <summary>
/// Resolves a spin-off's share count and basis split (§13.6: "cost basis allocated by the action's
/// ratio (fallback: first-print relative value)"). PURE.
///
/// THE SPEC IS GENUINELY UNDERSPECIFIED HERE, and the feed is DORMANT at launch (D49 — no spin-off
/// actions exist on live data through Phase 2), so this is a defensible, documented approximation
/// rather than a settled rule. What is NOT approximate — and is what the fixtures actually pin — is
/// that basis is conserved and a position is created; the exact split is the soft part.
///
/// INTERPRETATION (recorded as a stop-and-report seam in PROGRESS):
///  • `ratio` = spun-off shares received per parent share (the same new/old sense as a split).
///  • **By-ratio path (ratio present):** shares = parentShares × ratio; basis split share-
///    proportionally — basisToSpinoff = parentBasis × ratio/(1+ratio). This needs no prices, which is
///    why it is the primary path, but it is CRUDE: a share count is a poor proxy for value, so a
///    many-share small-value spin-off would be over-allocated. Prefer feeding the data that hits the
///    first-print path when the feed turns on.
///  • **First-print path (ratio missing):** shares fall back to 1:1 (parentShares), and basis is split
///    by RELATIVE FIRST-DAY VALUE — basisToSpinoff = parentBasis × spinoffValue/(parentValueAfter +
///    spinoffValue). This is the honest method (value, not share count), and it is why §13.6 names it
///    the fallback; it needs the first prints, which the adapter reads from the bar store.
/// </summary>
public static class SpinoffAllocation
{
    /// <summary>The by-ratio path (ratio present). No prices needed; share-proportional basis.</summary>
    public static SpinoffTerms ByRatio(double parentShares, decimal parentBasis, double ratio)
    {
        if (parentShares <= 0 || !double.IsFinite(parentShares))
            throw new ArgumentOutOfRangeException(nameof(parentShares), parentShares, "Parent shares must be positive.");
        if (ratio <= 0 || !double.IsFinite(ratio))
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Spin-off ratio must be positive and finite.");

        var spinoffShares = parentShares * ratio;
        // Share-proportional split: the spin-off's share of the combined share count.
        var fractionToSpinoff = (decimal)(ratio / (1.0 + ratio));
        return new SpinoffTerms(spinoffShares, RoundBasis(parentBasis * fractionToSpinoff));
    }

    /// <summary>The first-print fallback (ratio missing). Value-based basis; shares default to 1:1.</summary>
    public static SpinoffTerms ByFirstPrint(
        double parentShares, decimal parentBasis, double parentFirstPriceAfter, double spinoffFirstPrice)
    {
        if (parentShares <= 0 || !double.IsFinite(parentShares))
            throw new ArgumentOutOfRangeException(nameof(parentShares), parentShares, "Parent shares must be positive.");
        if (parentFirstPriceAfter <= 0 || !double.IsFinite(parentFirstPriceAfter))
            throw new ArgumentOutOfRangeException(nameof(parentFirstPriceAfter), parentFirstPriceAfter, "Parent first price must be positive.");
        if (spinoffFirstPrice <= 0 || !double.IsFinite(spinoffFirstPrice))
            throw new ArgumentOutOfRangeException(nameof(spinoffFirstPrice), spinoffFirstPrice, "Spin-off first price must be positive.");

        var spinoffShares = parentShares; // 1:1 receipt when the ratio is unknown (documented fallback)
        var spinoffValue = spinoffShares * spinoffFirstPrice;
        var parentValueAfter = parentShares * parentFirstPriceAfter;
        var fractionToSpinoff = (decimal)(spinoffValue / (parentValueAfter + spinoffValue));
        return new SpinoffTerms(spinoffShares, RoundBasis(parentBasis * fractionToSpinoff));
    }

    /// <summary>Round the allocated basis to the cent so the split lands on exact ledger money (D69);
    /// the parent keeps the remainder, so total basis is still conserved to the cent.</summary>
    private static decimal RoundBasis(decimal basis) => Math.Round(basis, 2, MidpointRounding.ToEven);
}
