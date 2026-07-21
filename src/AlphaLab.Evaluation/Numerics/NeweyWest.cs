namespace AlphaLab.Evaluation.Numerics;

/// <summary>
/// Result of a simple OLS regression y = α + β·x + ε with Newey–West (HAC) standard errors on the
/// coefficients. All fields are in the units of the inputs (per-observation).
/// </summary>
public readonly record struct OlsFit(double Alpha, double Beta, double AlphaSe, double BetaSe, int N, int Lag)
{
    /// <summary>Two-sided t-statistic for α ≠ 0 (0 when the SE is degenerate).</summary>
    public double AlphaT => AlphaSe > 0 ? Alpha / AlphaSe : 0.0;
}

/// <summary>
/// Newey–West long-run variance (Bartlett kernel) and the HAC-robust simple OLS both share one
/// autocovariance convention (D48 / MONITOR Appendix C). Pure and deterministic.
///
///   σ²_LR = γ₀ + 2·Σ_{k=1..L} (1 − k/(L+1))·γ_k        γ_k = (1/n) Σ (x_t−x̄)(x_{t−k}−x̄)
///
/// The lag L is the caller's Bartlett bandwidth (the gate uses L = min(2·maxHorizon, NwLagCapDays)).
/// A short series truncates the sum at n−1 lags but keeps the caller's L in the weight denominator so
/// the kernel shape is unchanged.
/// </summary>
public static class NeweyWest
{
    /// <summary>The NW long-run variance of a scalar series (the MDE's σ²_LR input). Floored at 0
    /// (the truncated estimator can go slightly negative in finite samples).</summary>
    public static double LongRunVariance(IReadOnlyList<double> series, int lag)
    {
        var n = series.Count;
        if (n < 2) return 0.0;

        var mean = 0.0;
        for (var i = 0; i < n; i++) mean += series[i];
        mean /= n;

        double Gamma(int k)
        {
            var s = 0.0;
            for (var t = k; t < n; t++) s += (series[t] - mean) * (series[t - k] - mean);
            return s / n;
        }

        var lrv = Gamma(0);
        var maxLag = Math.Min(lag, n - 1);
        for (var k = 1; k <= maxLag; k++)
        {
            var w = 1.0 - (double)k / (lag + 1);
            lrv += 2.0 * w * Gamma(k);
        }

        return Math.Max(lrv, 0.0);
    }

    /// <summary>
    /// Simple OLS y = α + β·x + ε with Bartlett-kernel HAC standard errors. The meat matrix is
    /// S = Γ₀ + Σ_{k=1..L} w_k (Γ_k + Γ_kᵀ) over the score vectors g_t = e_t·[1, x_t]; the sandwich is
    /// (ZᵀZ)⁻¹ S (ZᵀZ)⁻¹. Throws on n &lt; 3 or a degenerate (constant-x) design (fail loud).
    /// </summary>
    public static OlsFit Ols(IReadOnlyList<double> y, IReadOnlyList<double> x, int lag)
    {
        var n = y.Count;
        if (n != x.Count) throw new ArgumentException("y and x must be the same length.", nameof(x));
        if (n < 3) throw new ArgumentException("HAC OLS needs at least 3 observations.", nameof(y));

        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i];
        }

        // (ZᵀZ) = [[n, sx],[sx, sxx]]; its determinant and inverse.
        var det = n * sxx - sx * sx;
        if (Math.Abs(det) < 1e-12)
            throw new ArgumentException("Degenerate design (x has no variation) — β is unidentified.", nameof(x));

        var beta = (n * sxy - sx * sy) / det;
        var alpha = (sy - beta * sx) / n;

        // (ZᵀZ)⁻¹ = (1/det) [[sxx, -sx],[-sx, n]].
        double zi00 = sxx / det, zi01 = -sx / det, zi11 = n / det; // zi10 == zi01 (symmetric)

        // Score vectors g_t = e_t·[1, x_t]; accumulate Γ_0 and the lagged Γ_k (+ transpose) into S.
        var e = new double[n];
        for (var i = 0; i < n; i++) e[i] = y[i] - alpha - beta * x[i];

        double s00 = 0, s01 = 0, s11 = 0;
        for (var t = 0; t < n; t++)
        {
            var g0 = e[t];              // score component for the intercept
            var g1 = e[t] * x[t];       // score component for the slope
            s00 += g0 * g0; s01 += g0 * g1; s11 += g1 * g1;
        }

        var maxLag = Math.Min(lag, n - 1);
        for (var k = 1; k <= maxLag; k++)
        {
            var w = 1.0 - (double)k / (lag + 1);
            double c00 = 0, c01 = 0, c10 = 0, c11 = 0;
            for (var t = k; t < n; t++)
            {
                double gt0 = e[t], gt1 = e[t] * x[t];
                double gl0 = e[t - k], gl1 = e[t - k] * x[t - k];
                c00 += gt0 * gl0; c01 += gt0 * gl1; c10 += gt1 * gl0; c11 += gt1 * gl1;
            }
            // Add w·(Γ_k + Γ_kᵀ): the symmetric part contributes (c + cᵀ).
            s00 += w * (c00 + c00);
            s01 += w * (c01 + c10);
            s11 += w * (c11 + c11);
        }

        // Cov = (ZᵀZ)⁻¹ · S · (ZᵀZ)⁻¹, with S = [[s00, s01],[s01, s11]] and (ZᵀZ)⁻¹ symmetric.
        // M = (ZᵀZ)⁻¹ · S:
        double m00 = zi00 * s00 + zi01 * s01;
        double m01 = zi00 * s01 + zi01 * s11;
        double m10 = zi01 * s00 + zi11 * s01;
        double m11 = zi01 * s01 + zi11 * s11;
        // Cov = M · (ZᵀZ)⁻¹:
        double cov00 = m00 * zi00 + m01 * zi01;
        double cov11 = m10 * zi01 + m11 * zi11;

        var alphaSe = Math.Sqrt(Math.Max(cov00, 0.0));
        var betaSe = Math.Sqrt(Math.Max(cov11, 0.0));
        return new OlsFit(alpha, beta, alphaSe, betaSe, n, maxLag);
    }
}
