namespace AlphaLab.Evaluation.Numerics;

/// <summary>
/// Distribution helpers for the population bands + S3 percentile (D36). <see cref="Percentile"/> is the
/// linear-interpolation (PERCENTILE.INC) convention — the same one the regime labeler uses — so a
/// population's 5/25/50/75/95 band and a strategy's percentile rank speak one language. Pure.
/// </summary>
public static class Statistics
{
    /// <summary>The p-th percentile (0..100) of <paramref name="values"/> by linear interpolation between
    /// order statistics. Empty ⇒ NaN; a single value ⇒ that value.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        if (values.Count == 1) return values[0];

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var rank = Math.Clamp(p, 0.0, 100.0) / 100.0 * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        var frac = rank - lo;
        return sorted[lo] + frac * (sorted[hi] - sorted[lo]);
    }

    /// <summary>The percentile RANK (0..100) of <paramref name="x"/> within <paramref name="population"/>:
    /// the share strictly below, plus half the ties (the mid-rank convention — an unbiased estimate of the
    /// true quantile, so a value at the exact median reads ~50, not ~100). Empty population ⇒ NaN.</summary>
    public static double PercentileRank(IReadOnlyList<double> population, double x)
    {
        var n = population.Count;
        if (n == 0) return double.NaN;

        int below = 0, equal = 0;
        foreach (var v in population)
        {
            if (v < x) below++;
            else if (v == x) equal++;
        }
        return 100.0 * (below + 0.5 * equal) / n;
    }
}
