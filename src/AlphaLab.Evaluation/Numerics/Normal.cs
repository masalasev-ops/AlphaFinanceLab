namespace AlphaLab.Evaluation.Numerics;

/// <summary>
/// Standard-normal helpers. <see cref="InvCdf"/> is the inverse CDF (quantile / probit) via Peter
/// Acklam's rational approximation (relative error &lt; 1.15e-9 over the open interval), used for the
/// MDE's z_{1−α/2} + z_power and the deflated-Sharpe haircut. Pure and deterministic — no RNG, no clock.
/// </summary>
public static class Normal
{
    // Acklam's coefficients.
    private static readonly double[] A =
    [
        -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
        1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00,
    ];

    private static readonly double[] B =
    [
        -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
        6.680131188771972e+01, -1.328068155288572e+01,
    ];

    private static readonly double[] C =
    [
        -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
        -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00,
    ];

    private static readonly double[] D =
    [
        7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
        3.754408661907416e+00,
    ];

    /// <summary>Inverse standard-normal CDF. Domain (0,1); throws outside it (fail loud, rule 10).</summary>
    public static double InvCdf(double p)
    {
        if (p is <= 0.0 or >= 1.0 || double.IsNaN(p))
            throw new ArgumentOutOfRangeException(nameof(p), p, "InvCdf requires 0 < p < 1.");

        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        if (p < pLow)
        {
            var q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5]) /
                   ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1.0);
        }

        if (p <= pHigh)
        {
            var q = p - 0.5;
            var r = q * q;
            return (((((A[0] * r + A[1]) * r + A[2]) * r + A[3]) * r + A[4]) * r + A[5]) * q /
                   (((((B[0] * r + B[1]) * r + B[2]) * r + B[3]) * r + B[4]) * r + 1.0);
        }

        {
            var q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return -(((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5]) /
                    ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1.0);
        }
    }
}
