using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The shared price statistics (D43's σ and D50's vol component read the same code). These pin the
/// two conventions that are easy to get wrong silently: the N+1-closes window and the n−1 denominator.
/// </summary>
public class PriceStatisticsTests
{
    [Fact]
    public void DailyReturns_YieldsOneFewerValueThanPrices()
    {
        var returns = PriceStatistics.DailyReturns([100.0, 110.0, 99.0]);

        Assert.Equal(2, returns.Count);
        Assert.Equal(0.10, returns[0], 10);
        Assert.Equal(-0.10, returns[1], 10);
    }

    [Theory]
    [InlineData(new double[] { 100.0 })]
    [InlineData(new double[] { })]
    public void DailyReturns_NeedsTwoPrices(double[] prices)
    {
        Assert.Empty(PriceStatistics.DailyReturns(prices));
    }

    // A zero/negative price is corrupt (DataQualityGate rejects it upstream). Dividing by it would
    // manufacture an infinity that rides into a vol and then into a fill price.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void DailyReturns_RefusesToBuildAReturnAcrossACorruptPrice(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceStatistics.DailyReturns([100.0, bad, 100.0]));
    }

    /// <summary>Bessel's correction, pinned against a hand-computed value. Sample: 1,2,3,4 →
    /// mean 2.5, Σ(x−x̄)² = 5, n−1 = 3 → √(5/3) ≈ 1.290994. The population form (÷4) gives ≈1.118 —
    /// biased low, which on the D43 impact path means systematically under-charging.</summary>
    [Fact]
    public void SampleStdev_UsesTheBesselCorrection_NotThePopulationForm()
    {
        var stdev = PriceStatistics.SampleStdev([1.0, 2.0, 3.0, 4.0]);

        Assert.NotNull(stdev);
        Assert.Equal(1.2909944487, stdev.Value, 9);
        Assert.NotEqual(1.1180339887, stdev.Value, 9); // the population form, explicitly rejected
    }

    /// <summary>Below two values dispersion is undefined. Null, never 0.0 — a zero σ claims a riskless
    /// name and prices the D43 impact term at exactly nothing.</summary>
    [Fact]
    public void SampleStdev_BelowTwoValues_IsNull_NotZero()
    {
        Assert.Null(PriceStatistics.SampleStdev([42.0]));
        Assert.Null(PriceStatistics.SampleStdev([]));
    }

    [Fact]
    public void SampleStdev_OfAConstantSeries_IsZero_WhichIsARealAnswer()
    {
        // Distinct from the undefined case above: three identical values genuinely have no dispersion.
        Assert.Equal(0.0, PriceStatistics.SampleStdev([5.0, 5.0, 5.0]));
    }

    /// <summary>THE WINDOW CONVENTION: an N-session vol needs N returns, so N+1 closes. This is the
    /// off-by-one that would otherwise silently change a number nobody re-derives.</summary>
    [Fact]
    public void RealizedVolDaily_AnNSessionVolConsumesNPlusOneCloses()
    {
        var prices = new[] { 100.0, 101.0, 102.0, 103.0 }; // 4 closes -> 3 returns

        var vol = PriceStatistics.RealizedVolDaily(prices);
        var expected = PriceStatistics.SampleStdev(PriceStatistics.DailyReturns(prices));

        Assert.Equal(expected, vol);
        Assert.Equal(3, PriceStatistics.DailyReturns(prices).Count);
    }

    [Fact]
    public void RealizedVolDaily_IsNotAnnualized()
    {
        // ±1% alternating daily moves: a daily σ of ~1%, not ~16% (which is 1% × √252).
        var prices = new[] { 100.0, 101.0, 100.0, 101.0, 100.0, 101.0, 100.0 };

        var vol = PriceStatistics.RealizedVolDaily(prices);

        Assert.NotNull(vol);
        Assert.InRange(vol.Value, 0.005, 0.02);
    }

    [Fact]
    public void RealizedVolDaily_WithTooFewPrices_IsNull()
    {
        Assert.Null(PriceStatistics.RealizedVolDaily([100.0]));
        Assert.Null(PriceStatistics.RealizedVolDaily([100.0, 101.0])); // 1 return -> dispersion undefined
    }
}
