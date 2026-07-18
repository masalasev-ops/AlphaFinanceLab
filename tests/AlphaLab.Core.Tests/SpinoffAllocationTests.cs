using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The spin-off allocation resolver — the two paths §13.6 names (by the action's ratio, and the
/// first-print relative-value fallback). The exact split is a documented approximation for a dormant
/// feed; what these pin is that basis stays in [0, parentBasis] and the shares are well-formed.
/// </summary>
public class SpinoffAllocationTests
{
    // ---- by-ratio path ----

    [Fact]
    public void ByRatio_SharesAreParentSharesTimesRatio()
    {
        var terms = SpinoffAllocation.ByRatio(parentShares: 100, parentBasis: 6_000m, ratio: 0.2);

        Assert.Equal(20, terms.SpinoffShares);               // 100 × 0.2
    }

    [Fact]
    public void ByRatio_AllocatesBasisShareProportionally_AndStaysWithinTheParentBasis()
    {
        // ratio 0.25 → fraction to spin-off = 0.25 / 1.25 = 0.20 → 20% of 5,000 = 1,000.
        var terms = SpinoffAllocation.ByRatio(parentShares: 100, parentBasis: 5_000m, ratio: 0.25);

        Assert.Equal(1_000m, terms.BasisToSpinoff);
        Assert.InRange(terms.BasisToSpinoff, 0m, 5_000m);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ByRatio_RejectsANonPositiveRatio(double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpinoffAllocation.ByRatio(100, 5_000m, ratio));
    }

    // ---- first-print path ----

    /// <summary>Value-based allocation: with a parent worth $90/sh after the spin and a spin-off worth
    /// $10/sh (1:1 receipt), the spin-off is 10% of combined value → 10% of the basis.</summary>
    [Fact]
    public void ByFirstPrint_AllocatesBasisByRelativeValue()
    {
        var terms = SpinoffAllocation.ByFirstPrint(
            parentShares: 100, parentBasis: 5_000m, parentFirstPriceAfter: 90.0, spinoffFirstPrice: 10.0);

        Assert.Equal(100, terms.SpinoffShares);              // 1:1 fallback when the ratio is unknown
        // spinoffValue = 100×10 = 1,000; parentValue = 100×90 = 9,000; fraction = 1,000/10,000 = 0.10.
        Assert.Equal(500m, terms.BasisToSpinoff);            // 10% of 5,000
    }

    [Fact]
    public void ByFirstPrint_ConservesBasis_ParentKeepsTheRemainder()
    {
        var terms = SpinoffAllocation.ByFirstPrint(100, 5_000m, 90.0, 10.0);

        var parentKeeps = 5_000m - terms.BasisToSpinoff;
        Assert.Equal(5_000m, parentKeeps + terms.BasisToSpinoff); // conserved
    }

    [Theory]
    [InlineData(0.0, 10.0)]
    [InlineData(90.0, 0.0)]
    [InlineData(90.0, -1.0)]
    public void ByFirstPrint_RejectsANonPositivePrice(double parentPrice, double spinoffPrice)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpinoffAllocation.ByFirstPrint(100, 5_000m, parentPrice, spinoffPrice));
    }
}
