using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Tests;

public class NeweyWestTests
{
    // A deterministic AR(1) d_t = φ·d_{t-1} + ε_t with Box–Muller Gaussian shocks (fixed seed → the test
    // is reproducible without an RNG in production code).
    internal static double[] Ar1(int n, double phi, int seed)
    {
        var rng = new Random(seed);
        var d = new double[n];
        var prev = 0.0;
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            var eps = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            prev = phi * prev + eps;
            d[i] = prev;
        }
        return d;
    }

    [Fact]
    public void LongRunVariance_AtLagZero_IsTheSampleVariance()
    {
        double[] x = [1, 2, 3, 4, 5, 4, 3, 2, 1, 2];
        var mean = x.Average();
        var gamma0 = x.Select(v => (v - mean) * (v - mean)).Sum() / x.Length;   // /n convention
        Assert.Equal(gamma0, NeweyWest.LongRunVariance(x, 0), 10);
    }

    [Fact]
    public void LongRunVariance_PositiveAutocorrelation_ExceedsGamma0()
    {
        // AR(1) with φ=0.7 is strongly positively autocorrelated: the NW long-run variance (which adds
        // 2·Σ w_k·γ_k) must exceed γ0 (the i.i.d. variance) — the whole point of the D48 correction.
        var d = Ar1(600, 0.7, 20260720);
        var gamma0 = NeweyWest.LongRunVariance(d, 0);
        var lrv = NeweyWest.LongRunVariance(d, 21);
        Assert.True(lrv > gamma0, $"NW LRV {lrv} should exceed γ0 {gamma0} for a positively-autocorrelated series.");
    }

    [Fact]
    public void LongRunVariance_NeverNegative()
    {
        // A strongly negatively-autocorrelated series (perfect alternation) can push the raw estimator
        // below zero; the floor keeps it at 0 (a variance is never negative).
        double[] alt = [1, -1, 1, -1, 1, -1, 1, -1];
        Assert.True(NeweyWest.LongRunVariance(alt, 3) >= 0.0);
    }

    [Fact]
    public void Ols_RecoversExactLine_WhenNoNoise()
    {
        double[] x = [1, 2, 3, 4, 5, 6, 7];
        var y = x.Select(v => 2.0 + 3.0 * v).ToArray();     // α=2, β=3 exactly
        var fit = NeweyWest.Ols(y, x, 2);
        Assert.Equal(2.0, fit.Alpha, 9);
        Assert.Equal(3.0, fit.Beta, 9);
        Assert.Equal(0.0, fit.AlphaSe, 6);                  // zero residuals ⇒ zero SE
        Assert.Equal(0.0, fit.AlphaT, 6);                   // guarded to 0 when SE is degenerate
    }

    [Fact]
    public void Ols_NoisyFit_HasPositiveStandardErrors()
    {
        double[] x = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        double[] y = [2.1, 3.9, 6.2, 7.8, 10.3, 11.7, 14.2, 15.8, 18.1, 20.2]; // ≈ 3 + 1.7x + noise
        var fit = NeweyWest.Ols(y, x, 2);
        Assert.True(fit.Beta is > 1.5 and < 2.5);
        Assert.True(fit.AlphaSe > 0);
        Assert.True(fit.BetaSe > 0);
    }

    [Fact]
    public void Ols_DegenerateDesign_Throws()
    {
        double[] x = [5, 5, 5, 5];
        double[] y = [1, 2, 3, 4];
        Assert.Throws<ArgumentException>(() => NeweyWest.Ols(y, x, 1));
    }

    [Fact]
    public void Ols_TooFewObservations_Throws()
    {
        double[] x = [1, 2];
        double[] y = [1, 2];
        Assert.Throws<ArgumentException>(() => NeweyWest.Ols(y, x, 1));
    }
}
