namespace AlphaLab.Core.Domain;

/// <summary>
/// The price-series statistics the lab computes in more than one place, defined ONCE.
///
/// Why this exists as a shared type rather than a private helper: "21-day realized vol" appears in
/// D43's impact term (via <see cref="IFeatureView.RealizedVolDaily"/>) and again in D50's regime
/// volatility component (§20.1). Two implementations would silently answer differently the first
/// time one of them changed its denominator or its window convention — and the disagreement would
/// surface as a mispriced fill or a wrong regime label, neither of which points at its cause.
///
/// PURE, BCL only.
/// </summary>
public static class PriceStatistics
{
    /// <summary>
    /// Simple daily returns from a price series ordered oldest-first: r[i] = p[i]/p[i-1] − 1. Yields
    /// one fewer value than it is given; an empty or single-price series yields none.
    ///
    /// Throws on a non-positive or non-finite price rather than emitting a nonsense return: a zero or
    /// negative price is corrupt data (DataQualityGate rejects it upstream, rule 10), and dividing by
    /// it would manufacture an infinity that propagates into a vol and then into a fill price.
    /// </summary>
    public static IReadOnlyList<double> DailyReturns(IReadOnlyList<double> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        if (prices.Count < 2) return [];

        var returns = new List<double>(prices.Count - 1);
        for (var i = 1; i < prices.Count; i++)
        {
            var prev = prices[i - 1];
            if (!double.IsFinite(prev) || prev <= 0 || !double.IsFinite(prices[i]) || prices[i] <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(prices),
                    $"price at index {(double.IsFinite(prev) && prev > 0 ? i : i - 1)} is not finite and positive — " +
                    "a return cannot be built across a corrupt price.");
            }
            returns.Add(prices[i] / prev - 1.0);
        }
        return returns;
    }

    /// <summary>
    /// Sample standard deviation (n−1 denominator, sample mean subtracted). Null below two values,
    /// where dispersion is undefined — returning 0.0 there would claim a riskless name and hand the
    /// D43 impact term a σ of zero, pricing a fill as free.
    ///
    /// n−1 (Bessel) rather than n: these are samples of an unknown process, not a population, and n
    /// biases σ low — which on the impact path means systematically under-charging.
    /// </summary>
    public static double? SampleStdev(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2) return null;

        var mean = values.Average();
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        return System.Math.Sqrt(sumSq / (values.Count - 1));
    }

    /// <summary>
    /// Realized DAILY volatility: the sample stdev of the simple daily returns of
    /// <paramref name="prices"/> (oldest-first). Not annualized — the D43 impact term wants a daily
    /// σ, and D50's vol component compares a daily σ against its own trailing distribution, so
    /// annualizing here would only invite someone to divide it back out.
    ///
    /// WINDOW CONVENTION — stated because it is the kind of off-by-one that silently changes a
    /// number nobody re-derives: an N-session vol needs N returns, which needs N+1 closes. A caller
    /// asking for a 21-day vol must hand over 22 prices. Null when there are fewer than two prices.
    /// </summary>
    public static double? RealizedVolDaily(IReadOnlyList<double> prices) =>
        SampleStdev(DailyReturns(prices));
}
