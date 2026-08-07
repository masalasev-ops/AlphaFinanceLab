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
    /// true quantile, so a value at the exact median reads ~50, not ~100). Empty population ⇒ NaN.
    ///
    /// DELEGATES to <see cref="AlphaLab.Core.Numerics.Ranking.PercentileRank"/> (6.3): the same notion is
    /// needed by the strategy models, and <c>AlphaLab.Strategies</c> cannot reference this assembly — so the
    /// definition moved to Core and this stays as the 0..100 PRESENTATION of it. Deliberately not a second
    /// copy: a rule written twice is two definitions that drift, and S3's percentile is a verdict input.</summary>
    public static double PercentileRank(IReadOnlyList<double> population, double x) =>
        100.0 * AlphaLab.Core.Numerics.Ranking.PercentileRank(population, x);

    /// <summary>
    /// Fractional MID-RANKS (1-based) of <paramref name="values"/>, ties sharing their average rank —
    /// the same convention <see cref="PercentileRank"/> already uses, so the library's two ranking
    /// notions agree rather than differing by a tie rule nobody wrote down.
    ///
    /// Mid-ranks are what makes <see cref="SpearmanRankCorrelation"/> correct in the presence of ties,
    /// and ties are ordinary here: <c>brk:L252</c> saturates at 1.0 for every name at its high, and
    /// <c>rev:L21</c> can repeat exactly across a flat cross-section. Ordinal ranking with an id
    /// tiebreak — the convention the funnel's selection uses for a different and good reason — would
    /// invent an ordering the data does not contain and bias the correlation.
    /// </summary>
    public static double[] MidRanks(IReadOnlyList<double> values)
    {
        var n = values.Count;
        var ranks = new double[n];
        if (n == 0) return ranks;

        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

        var i = 0;
        while (i < n)
        {
            var j = i;
            while (j + 1 < n && values[order[j + 1]] == values[order[i]]) j++;
            // Ranks are 1-based; the tie block [i..j] shares the average of its positions.
            var shared = (i + j) / 2.0 + 1.0;
            for (var k = i; k <= j; k++) ranks[order[k]] = shared;
            i = j + 1;
        }
        return ranks;
    }

    /// <summary>
    /// Spearman rank correlation between two paired series — the Pearson correlation of their
    /// <see cref="MidRanks"/>. This is the rank-IC the Signal Library grades with (D91, §24.2).
    ///
    /// Returns null rather than a number when the correlation is undefined: fewer than two pairs, a
    /// length mismatch, or a series with no rank dispersion at all (every value tied ⇒ zero variance).
    /// A degenerate cross-section has no information content, and reporting 0.0 for it would enter the
    /// rolling mean as though it were evidence of no skill rather than an absence of evidence.
    /// </summary>
    public static double? SpearmanRankCorrelation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count || a.Count < 2) return null;

        var ra = MidRanks(a);
        var rb = MidRanks(b);
        double ma = 0, mb = 0;
        for (var i = 0; i < ra.Length; i++) { ma += ra[i]; mb += rb[i]; }
        ma /= ra.Length; mb /= rb.Length;

        double cov = 0, va = 0, vb = 0;
        for (var i = 0; i < ra.Length; i++)
        {
            var da = ra[i] - ma;
            var db = rb[i] - mb;
            cov += da * db;
            va += da * da;
            vb += db * db;
        }
        if (va <= 0 || vb <= 0) return null;   // no dispersion on one side ⇒ undefined, not zero
        return cov / Math.Sqrt(va * vb);
    }
}
