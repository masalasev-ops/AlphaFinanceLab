namespace AlphaLab.Evaluation.Numerics;

/// <summary>
/// Student-t distribution: CDF and quantile (inverse CDF).
///
/// WHY THIS EXISTS RATHER THAN REUSING <see cref="Normal"/> (D108). The Signal Library's trend flag is
/// inferred on ~20 effective independent observations, and a NORMAL reference is wrong at that sample
/// size: t(df=19) = 2.093 against z = 1.960, and at the ~4 observations the rejected 1-year window
/// carried it is t(df=3) = 3.182 against the same 1.960 — a nominal 5 % test with an actual size near
/// 15 %. The reference distribution is therefore part of the verdict, not a refinement of it.
///
/// WHY IT IS COMPUTED RATHER THAN APPROXIMATED. The Cornish–Fisher expansion from the normal quantile
/// is accurate to ~5 decimals at df ≈ 20 but drifts to ~0.1 % by df ≈ 3. Since D108 makes the critical
/// value a load-bearing INPUT to a published flag, this uses the regularized incomplete beta function
/// (Lentz continued fraction) and inverts by bisection — exact to tolerance at every df, including the
/// small ones a future window change would reach. Pure; no state; no allocation beyond locals.
/// </summary>
public static class StudentT
{
    private const double Epsilon = 1e-14;
    private const double Tiny = 1e-300;

    /// <summary>
    /// P(T ≤ t) for <paramref name="df"/> degrees of freedom, via the symmetry relation
    /// P(T ≤ −|t|) = ½·I_{df/(df+t²)}(df/2, ½).
    /// </summary>
    public static double Cdf(double t, double df)
    {
        if (df <= 0) throw new ArgumentOutOfRangeException(nameof(df), df, "Degrees of freedom must be positive.");
        if (double.IsNaN(t)) return double.NaN;
        if (double.IsPositiveInfinity(t)) return 1.0;
        if (double.IsNegativeInfinity(t)) return 0.0;

        var x = df / (df + t * t);
        var tail = 0.5 * RegularizedIncompleteBeta(0.5 * df, 0.5, x);
        return t > 0 ? 1.0 - tail : tail;
    }

    /// <summary>
    /// The <paramref name="p"/>-quantile: the t with <c>Cdf(t, df) = p</c>. Bisection on a bracket
    /// widened until it contains the root, so it is correct for any df rather than tuned to one range.
    /// </summary>
    public static double InvCdf(double p, double df)
    {
        if (p is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(p), p, "p must be in (0,1).");
        if (df <= 0) throw new ArgumentOutOfRangeException(nameof(df), df, "Degrees of freedom must be positive.");

        double lo = -1.0, hi = 1.0;
        while (Cdf(lo, df) > p) lo *= 2.0;
        while (Cdf(hi, df) < p) hi *= 2.0;

        for (var i = 0; i < 200 && hi - lo > 1e-12 * Math.Max(1.0, Math.Abs(hi)); i++)
        {
            var mid = 0.5 * (lo + hi);
            if (Cdf(mid, df) < p) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// The TWO-SIDED critical value at significance level <paramref name="alpha"/>:
    /// <c>t_{1−α/2, df}</c>. This is what the trend flag compares a statistic against, and the reason
    /// the pinned config constant is α rather than the critical value itself (D108) — the critical
    /// value depends on df, which depends on the effective sample, so it cannot be authored in advance.
    /// </summary>
    public static double TwoSidedCritical(double alpha, double df)
    {
        if (alpha is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be in (0,1).");
        return InvCdf(1.0 - alpha / 2.0, df);
    }

    /// <summary>The ONE-SIDED critical value at significance level <paramref name="alpha"/>:
    /// <c>t_{1−α, df}</c>. The trend flag's arms are directional (decaying = significantly NEGATIVE;
    /// gone = not significantly ABOVE zero), so one-sided is the honest test for them.</summary>
    public static double OneSidedCritical(double alpha, double df)
    {
        if (alpha is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be in (0,1).");
        return InvCdf(1.0 - alpha, df);
    }

    /// <summary>
    /// The regularized incomplete beta I_x(a,b), by the standard continued-fraction expansion with the
    /// symmetry reflection that keeps it in its fast-converging region.
    /// </summary>
    private static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0) return 0.0;
        if (x >= 1) return 1.0;

        var lnFront = LogGamma(a + b) - LogGamma(a) - LogGamma(b)
                      + a * Math.Log(x) + b * Math.Log(1.0 - x);
        var front = Math.Exp(lnFront);

        // Converges rapidly for x < (a+1)/(a+b+2); otherwise use I_x(a,b) = 1 − I_{1−x}(b,a).
        return x < (a + 1.0) / (a + b + 2.0)
            ? front * ContinuedFraction(a, b, x) / a
            : 1.0 - RegularizedIncompleteBeta(b, a, 1.0 - x);
    }

    /// <summary>Lentz's modified continued fraction for the incomplete beta.</summary>
    private static double ContinuedFraction(double a, double b, double x)
    {
        var qab = a + b;
        var qap = a + 1.0;
        var qam = a - 1.0;
        var c = 1.0;
        var d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < Tiny) d = Tiny;
        d = 1.0 / d;
        var h = d;

        for (var m = 1; m <= 300; m++)
        {
            var m2 = 2 * m;

            // Even step.
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1.0 / d;
            h *= d * c;

            // Odd step.
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < Tiny) d = Tiny;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < Tiny) c = Tiny;
            d = 1.0 / d;
            var delta = d * c;
            h *= delta;

            if (Math.Abs(delta - 1.0) < Epsilon) break;
        }
        return h;
    }

    /// <summary>Lanczos approximation (g = 7, n = 9) to log Γ(z) — ~15 significant digits for z &gt; 0,
    /// with the reflection formula below ½. Pinned by fixture against log Γ(½) = ln√π and log Γ(5) = ln 24.</summary>
    private static double LogGamma(double z)
    {
        double[] c =
        [
            676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7,
        ];

        if (z < 0.5) return Math.Log(Math.PI / Math.Sin(Math.PI * z)) - LogGamma(1.0 - z);

        z -= 1.0;
        var x = 0.99999999999980993;
        for (var i = 0; i < c.Length; i++) x += c[i] / (z + i + 1.0);
        var t = z + c.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (z + 0.5) * Math.Log(t) - t + Math.Log(x);
    }
}
