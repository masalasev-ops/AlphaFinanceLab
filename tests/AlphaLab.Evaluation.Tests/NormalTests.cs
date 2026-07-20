using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Tests;

/// <summary>The inverse-normal quantile underpins the MDE's z-sum and the deflated-Sharpe haircut, so
/// its known quantiles are pinned to reference values.</summary>
public class NormalTests
{
    [Theory]
    [InlineData(0.975, 1.959963985)]   // z_{1-0.05/2}
    [InlineData(0.80, 0.841621234)]    // z_{0.80} (power)
    [InlineData(0.95, 1.644853627)]
    [InlineData(0.99, 2.326347874)]
    public void InvCdf_MatchesKnownQuantiles(double p, double expected)
    {
        Assert.Equal(expected, Normal.InvCdf(p), 6);
    }

    [Fact]
    public void InvCdf_AtHalf_IsZero() => Assert.Equal(0.0, Normal.InvCdf(0.5), 9);

    [Fact]
    public void InvCdf_IsAntisymmetric()
    {
        Assert.Equal(-Normal.InvCdf(0.9), Normal.InvCdf(0.1), 6);
        Assert.Equal(-Normal.InvCdf(0.995), Normal.InvCdf(0.005), 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void InvCdf_OutsideOpenUnitInterval_Throws(double p) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Normal.InvCdf(p));
}
