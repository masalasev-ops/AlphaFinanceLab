using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Numerics;

namespace AlphaLab.Core.Tests;

/// <summary>
/// FX-PercentileRankConvention — the `percentile_rank` primitive STRATEGY_CATALOG uses in four scoring
/// blocks and never defines (§6.1, §6.3, §6.5, §6.7). Checkpoint 6.3 rules it: MID-RANK, in [0,1].
///
/// The load-bearing fixture is <see cref="PercentileRank_NeverEmitsZero_SoTheWorstNameIsLastNotUnselectable"/>:
/// under the `(rank-1)/(n-1)` alternative the bottom name scores exactly 0.0 and hard rule 7 refuses it
/// with a reason that is FALSE — it was scored legitimately and merely came last.
/// </summary>
public class RankingTests
{
    private static SecurityId S(long id) => new(id);

    private static GuardrailsOptions Rails() => new() { MinScore = 0.0, MaxConcurrentPositions = 1000 };

    [Fact]
    public void PercentileRank_IsMidRank_SoTiesShareTheirScoreAndTheMedianReadsHalf()
    {
        // No ties: the middle of three reads exactly 0.5, not 0.33 or 0.67.
        Assert.Equal(0.5, Ranking.PercentileRank([1.0, 2.0, 3.0], 2.0), 12);

        // A tie block shares (below + half the ties) / n — the unbiased quantile estimate.
        Assert.Equal(1.0 / 3.0, Ranking.PercentileRank([1.0, 1.0, 2.0], 1.0), 12);

        // Endpoints of the SCALAR form: a value outside the population may legitimately read 0 or 1,
        // because it is not a member of the cross-section being ranked.
        Assert.Equal(0.0, Ranking.PercentileRank([1.0, 2.0, 3.0], 0.5), 12);
        Assert.Equal(1.0, Ranking.PercentileRank([1.0, 2.0, 3.0], 9.9), 12);

        // No cross-section ⇒ no rank. NaN, never 0 — 0 would be a fabricated "worst" verdict.
        Assert.True(double.IsNaN(Ranking.PercentileRank([], 1.0)));
    }

    [Fact]
    public void PercentileRanks_CrossSection_AgreesWithTheScalarFormNameByName()
    {
        // The O(n log n) mid-rank walk and the O(n^2) definition must be the SAME number, not merely
        // close — the arithmetic identity (midRank - 0.5)/n is asserted rather than claimed in a comment.
        var values = new Dictionary<SecurityId, double>
        {
            [S(4)] = 0.10, [S(1)] = 0.30, [S(7)] = 0.30, [S(2)] = -0.05, [S(9)] = 0.30, [S(3)] = 0.99,
        };
        var population = values.Values.ToList();

        var ranks = Ranking.PercentileRanks(values);

        Assert.Equal(values.Count, ranks.Count);
        foreach (var (id, v) in values)
        {
            Assert.Equal(Ranking.PercentileRank(population, v), ranks[id], 12);
        }

        // The three names tied at 0.30 share one score — a rank refuses to invent an order the data
        // does not contain. That is precisely why the Stage-3 seeded tie-break exists downstream.
        Assert.Equal(ranks[S(1)], ranks[S(7)]);
        Assert.Equal(ranks[S(1)], ranks[S(9)]);
    }

    [Fact]
    public void PercentileRank_NeverEmitsZero_SoTheWorstNameIsLastNotUnselectable()
    {
        var values = new Dictionary<SecurityId, double>
        {
            [S(1)] = -0.20, [S(2)] = 0.00, [S(3)] = 0.05, [S(4)] = 0.40,
        };

        var ranks = Ranking.PercentileRanks(values);

        // The bottom of the cross-section: 0.5/n, strictly positive. Under (rank-1)/(n-1) this is 0.0.
        Assert.Equal(0.5 / 4.0, ranks[S(1)], 12);
        Assert.True(ranks.Values.All(v => v > 0.0 && v < 1.0));

        // ...and the consequence that makes the convention load-bearing rather than cosmetic: every
        // ranked name survives the zero-score invariant, so rule 7 fires only on names the MODEL
        // declined to score, never on the tail of an ordinary cross-section.
        var selected = Selection.Select(ranks, SelectionRule.TopN(10) with { MinScore = 0.0 }, Rails(), seed: 0);
        Assert.Equal(4, selected.WishList.Count);
        Assert.Empty(selected.Excluded);
    }

    [Fact]
    public void PercentileRanks_KeepsNaNAsNaN_RatherThanRankingAnUnscoreableNameLast()
    {
        var values = new Dictionary<SecurityId, double>
        {
            [S(1)] = 0.10, [S(2)] = double.NaN, [S(3)] = 0.20,
        };

        var ranks = Ranking.PercentileRanks(values);

        Assert.True(double.IsNaN(ranks[S(2)]));
        // The scoreable names rank among THEMSELVES (m = 2), so the unscoreable one does not silently
        // enlarge the cross-section it was never part of.
        Assert.Equal(0.25, ranks[S(1)], 12);
        Assert.Equal(0.75, ranks[S(3)], 12);

        // NaN is excluded by the zero-score invariant, with rule 7's reason — the honest outcome.
        var selected = Selection.Select(ranks, SelectionRule.TopN(10) with { MinScore = 0.0 }, Rails(), seed: 0);
        Assert.DoesNotContain(S(2), selected.WishList);
    }

    [Fact]
    public void PercentileRanks_IsDeterministic_RegardlessOfDictionaryInsertionOrder()
    {
        // F-DET: Array.Sort is not stable, so an unordered read could permute equal values between runs.
        var a = new Dictionary<SecurityId, double> { [S(1)] = 1.0, [S(2)] = 1.0, [S(3)] = 0.5 };
        var b = new Dictionary<SecurityId, double> { [S(3)] = 0.5, [S(2)] = 1.0, [S(1)] = 1.0 };

        var ra = Ranking.PercentileRanks(a);
        var rb = Ranking.PercentileRanks(b);

        foreach (var id in new[] { S(1), S(2), S(3) }) Assert.Equal(ra[id], rb[id], 15);
    }

    [Fact]
    public void PercentileRanks_EmptyCrossSection_IsEmpty_NotAThrow()
    {
        Assert.Empty(Ranking.PercentileRanks(new Dictionary<SecurityId, double>()));
    }
}
