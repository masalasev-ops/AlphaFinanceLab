using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Numerics;

/// <summary>
/// The ONE definition of <c>percentile_rank</c> (Phase 6 checkpoint 6.3).
///
/// STRATEGY_CATALOG uses <c>percentile_rank(...)</c> as a primitive in four scoring blocks (§6.1
/// momentum, §6.3 low-vol as <c>1 - percentile_rank(vol)</c>, §6.5 residual momentum, §6.7 blended)
/// and NEVER defines it — not the tie rule, not the endpoints, not the range. This type closes that,
/// and it lives in <c>AlphaLab.Core</c> for a structural reason: <c>AlphaLab.Strategies</c> does not
/// reference <c>AlphaLab.Evaluation</c>, so the existing
/// <c>Evaluation.Numerics.Statistics.PercentileRank</c> is UNREACHABLE from the models that need it.
/// A second copy there would be a second definition — the <c>MonitorRecompute</c> doctrine — so
/// <c>Statistics.PercentileRank</c> now delegates here and presents the same number on its own 0..100
/// scale.
///
/// **THE CONVENTION: mid-rank, in [0,1].** The share strictly below, plus half the ties, over n. Three
/// reasons it is this and not the <c>(rank-1)/(n-1)</c> alternative:
///
/// 1. **It is what the corpus already does.** <c>Statistics.PercentileRank</c> and
///    <c>Statistics.MidRanks</c> were deliberately made to agree "so the library's two ranking notions
///    agree rather than differing by a tie rule nobody wrote down". A third convention in the funnel
///    would re-open exactly what that comment closed.
///
/// 2. **It never emits 0, and that is load-bearing against hard rule 7.** Under
///    <c>(rank-1)/(n-1)</c> the worst-ranked name scores EXACTLY 0.0, and <c>Selection</c> then refuses
///    it with "score 0 is not &gt; 0 — never selectable (rule 7)". That reason would be false: the name
///    was scored perfectly legitimately and merely came last. Rule 7 exists so a strategy cannot pad a
///    wish list it did not earn; making it fire on the tail of every cross-section would turn an
///    honesty rail into a silent off-by-one. Here the minimum attainable score is <c>0.5/n &gt; 0</c> —
///    last, not excluded.
///
/// 3. **It never emits 1.0 either** (<c>1 - 0.5/n</c>), so <c>1 - percentile_rank</c> — low-vol's
///    literal catalog formula — is strictly positive too. No mode needs a special case at either end.
///
/// Ties share their score, which is the honest answer and is also why the Stage-3 SEEDED TIE-BREAK
/// exists: a percentile rank refuses to invent an order the data does not contain, so the ordering
/// among equals is settled downstream, once, by a rule that is written down.
///
/// PURE — no DB, no clock, no I/O.
/// </summary>
public static class Ranking
{
    /// <summary>
    /// The mid-rank percentile of <paramref name="x"/> within <paramref name="population"/>, in [0,1]:
    /// the share strictly below plus half the ties. An EMPTY population is NaN, not 0 — there is no
    /// cross-section to rank against, and NaN is the value <see cref="Funnel.Selection"/> already
    /// treats as unscoreable (every comparison against NaN is false).
    /// </summary>
    public static double PercentileRank(IReadOnlyList<double> population, double x)
    {
        ArgumentNullException.ThrowIfNull(population);

        var n = population.Count;
        if (n == 0) return double.NaN;

        int below = 0, equal = 0;
        foreach (var v in population)
        {
            if (v < x) below++;
            else if (v == x) equal++;
        }
        return (below + 0.5 * equal) / n;
    }

    /// <summary>
    /// The CROSS-SECTIONAL form: every name scored by its own mid-rank percentile within the day's
    /// scored universe. This is what the catalog's <c>score[s] = percentile_rank(ret[s])</c> means —
    /// one pass over one day's names, not a value looked up against a separate population.
    ///
    /// Computed from sorted mid-ranks (O(n log n)) rather than by calling
    /// <see cref="PercentileRank"/> per name (O(n²)); the identity <c>(midRank - 0.5) / n</c> makes the
    /// two definitionally the same number, and <c>FX-PercentileRankConvention</c> pins that agreement
    /// rather than leaving it as an unstated arithmetic claim.
    ///
    /// A NaN input value stays NaN in the output: an unscoreable name is not silently ranked last,
    /// which would be a fabricated score. An EMPTY cross-section returns an empty map.
    /// </summary>
    public static IReadOnlyDictionary<SecurityId, double> PercentileRanks(
        IReadOnlyDictionary<SecurityId, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        // Deterministic input order (F-DET): a dictionary has no guaranteed enumeration order, and
        // Array.Sort is not stable, so an unordered read could permute equal values between runs. The
        // SCORES are tie-insensitive, but the intermediate array is not — order by id first.
        var names = values.Keys.OrderBy(id => id.Value).ToArray();
        var n = names.Length;
        var result = new Dictionary<SecurityId, double>(n);
        if (n == 0) return result;

        // Rank only the scoreable names; NaN is not comparable and would poison the sort.
        var scoreable = names.Where(id => !double.IsNaN(values[id])).ToArray();
        foreach (var id in names)
        {
            if (double.IsNaN(values[id])) result[id] = double.NaN;
        }

        var m = scoreable.Length;
        if (m == 0) return result;

        var order = Enumerable.Range(0, m).ToArray();
        Array.Sort(order, (a, b) => values[scoreable[a]].CompareTo(values[scoreable[b]]));

        var i = 0;
        while (i < m)
        {
            // The tie block [i..j] shares the average of its 1-based positions.
            var j = i;
            while (j + 1 < m && values[scoreable[order[j + 1]]] == values[scoreable[order[i]]]) j++;
            var midRank = (i + j) / 2.0 + 1.0;
            var pct = (midRank - 0.5) / m;
            for (var k = i; k <= j; k++) result[scoreable[order[k]]] = pct;
            i = j + 1;
        }

        return result;
    }
}
