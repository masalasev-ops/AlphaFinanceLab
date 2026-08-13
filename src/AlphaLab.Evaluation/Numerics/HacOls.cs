namespace AlphaLab.Evaluation.Numerics;

/// <summary>
/// Result of a k-regressor OLS <c>y = α + Σ βⱼ·xⱼ + ε</c> with Newey–West (HAC) standard errors.
/// All fields are in the units of the inputs (per-observation), matching <see cref="OlsFit"/>.
/// </summary>
public readonly record struct HacOlsFit(
    double Alpha,
    IReadOnlyList<double> Betas,
    double AlphaSe,
    IReadOnlyList<double> BetaSes,
    int N,
    int Lag)
{
    /// <summary>Two-sided t-statistic for α ≠ 0 (0 when the SE is degenerate). Same convention as
    /// <see cref="OlsFit.AlphaT"/>.</summary>
    public double AlphaT => AlphaSe > 0 ? Alpha / AlphaSe : 0.0;

    /// <summary>Two-sided t-statistic for βⱼ ≠ 0 (0 when the SE is degenerate).</summary>
    public double BetaT(int j) => BetaSes[j] > 0 ? Betas[j] / BetaSes[j] : 0.0;
}

/// <summary>
/// HAC-robust OLS for ARBITRARILY MANY regressors — the general case of <see cref="NeweyWest.Ols"/>,
/// which is hard-coded to exactly one. Same Bartlett-kernel convention (D48 / MONITOR Appendix C), same
/// truncation rule, same degenerate-design fail-loud posture. Pure and deterministic.
///
///   Cov(b) = (ZᵀZ)⁻¹ · S · (ZᵀZ)⁻¹        S = Γ₀ + Σ_{k=1..L} w_k (Γ_k + Γ_kᵀ)
///   Γ_k = Σ_t g_t g_{t−k}ᵀ                g_t = e_t · z_t        w_k = 1 − k/(L+1)
///
/// **WHY THIS EXISTS RATHER THAN AN EDIT TO <see cref="NeweyWest"/>.** DESIGN_IMPROVEMENTS §1.4's
/// attribution regression has FIVE regressors, and `NeweyWest.Ols` cannot carry them: it inverts a 2×2
/// `(ZᵀZ)` in closed form, accumulates a 3-scalar meat matrix and hand-expands the sandwich. Generalizing
/// it in place would rewrite the arithmetic under every existing caller — the gate, the MDE, the monitor's
/// alpha t-stat — for a feature none of them asked for. So `NeweyWest.Ols` is left EXACTLY as it is and
/// becomes this type's ORACLE instead (see the k=1 fixture): a known-good closed form that a wrong matrix
/// path cannot agree with by accident.
///
/// **THE LAG IS DERIVED, NOT AUTHORED (finding 309).** §1.4 specifies Newey–West errors and states no
/// bandwidth; the only rule in the corpus is the gate's `L = min(2·maxHorizon, NwLagCapDays)`, written for
/// a horizon-keyed comparison. Attribution has NO horizon — it is a diagnostic over a strategy's whole
/// track — so that rule degenerates to its cap, and the caller passes `GateOptions.NwLagCapDays`. This is
/// deliberately a READ of the existing config key rather than a fourth `DefaultLag = 21` constant: three
/// already exist (`OverfittingMonitor.cs:40`, `ReplayRegimeOutcomesWriter.cs:18`,
/// `SeedingBacktestEngine.cs:34`), each re-authoring the value of a key that is right there, and adding a
/// fourth would be the finding-309 defect committed inside the fix for it.
/// </summary>
public static class HacOls
{
    /// <summary>
    /// Fit <c>y = α + Σ βⱼ·xⱼ + ε</c> with Bartlett-kernel HAC standard errors.
    /// </summary>
    /// <param name="y">The regressand, length n.</param>
    /// <param name="regressors">One series per regressor, each length n. Empty ⇒ intercept only.</param>
    /// <param name="lag">The caller's Bartlett bandwidth L. Kept in the weight denominator even when the
    /// sum truncates at n−1, so the kernel SHAPE is unchanged on a short series — the
    /// <see cref="NeweyWest"/> convention, matched deliberately.</param>
    /// <exception cref="ArgumentException">On a length mismatch, too few observations, or a rank-deficient
    /// design. Fails loud rather than returning a fit nobody can interpret.</exception>
    public static HacOlsFit Fit(IReadOnlyList<double> y, IReadOnlyList<IReadOnlyList<double>> regressors, int lag)
    {
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(regressors);

        var n = y.Count;
        var k = regressors.Count;
        var p = k + 1;                       // + the intercept column

        for (var j = 0; j < k; j++)
        {
            if (regressors[j].Count != n)
            {
                throw new ArgumentException(
                    $"Regressor {j} has {regressors[j].Count} observations but y has {n}.", nameof(regressors));
            }
        }

        // n < p + 1 leaves no residual degrees of freedom. At k = 1 this is n < 3 — the same bar
        // NeweyWest.Ols sets, so the general path refuses exactly where the closed form refuses.
        if (n < p + 1)
        {
            throw new ArgumentException(
                $"HAC OLS with {k} regressor(s) needs at least {p + 1} observations; got {n}.", nameof(y));
        }

        // The design matrix, materialised: z[t][0] = 1, z[t][j+1] = xⱼ(t).
        var z = new double[n][];
        for (var t = 0; t < n; t++)
        {
            var row = new double[p];
            row[0] = 1.0;
            for (var j = 0; j < k; j++) row[j + 1] = regressors[j][t];
            z[t] = row;
        }

        // (ZᵀZ) and (Zᵀy).
        var ztz = new double[p, p];
        var zty = new double[p];
        for (var t = 0; t < n; t++)
        {
            var row = z[t];
            var yt = y[t];
            for (var a = 0; a < p; a++)
            {
                zty[a] += row[a] * yt;
                for (var b = a; b < p; b++) ztz[a, b] += row[a] * row[b];
            }
        }
        for (var a = 0; a < p; a++)
        {
            for (var b = 0; b < a; b++) ztz[a, b] = ztz[b, a];
        }

        var chol = Cholesky(ztz, p);         // throws on a rank-deficient design
        var beta = CholSolve(chol, zty, p);
        var inv = CholInverse(chol, p);      // (ZᵀZ)⁻¹, symmetric

        // Residuals and score vectors g_t = e_t · z_t.
        var g = new double[n][];
        for (var t = 0; t < n; t++)
        {
            var row = z[t];
            var fitted = 0.0;
            for (var a = 0; a < p; a++) fitted += beta[a] * row[a];
            var e = y[t] - fitted;

            var gt = new double[p];
            for (var a = 0; a < p; a++) gt[a] = e * row[a];
            g[t] = gt;
        }

        // The meat: S = Γ₀ + Σ_k w_k (Γ_k + Γ_kᵀ).
        var s = new double[p, p];
        for (var t = 0; t < n; t++)
        {
            var gt = g[t];
            for (var a = 0; a < p; a++)
            {
                for (var b = 0; b < p; b++) s[a, b] += gt[a] * gt[b];
            }
        }

        var maxLag = Math.Min(lag, n - 1);
        for (var kk = 1; kk <= maxLag; kk++)
        {
            var w = 1.0 - (double)kk / (lag + 1);
            var c = new double[p, p];
            for (var t = kk; t < n; t++)
            {
                var gt = g[t];
                var gl = g[t - kk];
                for (var a = 0; a < p; a++)
                {
                    for (var b = 0; b < p; b++) c[a, b] += gt[a] * gl[b];
                }
            }
            // w·(Γ_k + Γ_kᵀ).
            for (var a = 0; a < p; a++)
            {
                for (var b = 0; b < p; b++) s[a, b] += w * (c[a, b] + c[b, a]);
            }
        }

        // Cov = (ZᵀZ)⁻¹ · S · (ZᵀZ)⁻¹. Only the diagonal is needed, but the full product is cheap at
        // p ≤ 7 and reads as the formula rather than as an optimisation of it.
        var m = new double[p, p];
        for (var a = 0; a < p; a++)
        {
            for (var b = 0; b < p; b++)
            {
                var acc = 0.0;
                for (var c2 = 0; c2 < p; c2++) acc += inv[a, c2] * s[c2, b];
                m[a, b] = acc;
            }
        }

        var se = new double[p];
        for (var a = 0; a < p; a++)
        {
            var acc = 0.0;
            for (var c2 = 0; c2 < p; c2++) acc += m[a, c2] * inv[c2, a];
            se[a] = Math.Sqrt(Math.Max(acc, 0.0));   // the truncated estimator can go slightly negative
        }

        var betas = new double[k];
        var betaSes = new double[k];
        for (var j = 0; j < k; j++)
        {
            betas[j] = beta[j + 1];
            betaSes[j] = se[j + 1];
        }

        return new HacOlsFit(beta[0], betas, se[0], betaSes, n, maxLag);
    }

    // ---- Cholesky on the symmetric positive-definite (ZᵀZ). ----
    //
    // The pivot floor is RELATIVE to the original diagonal, not an absolute epsilon: (ZᵀZ) entries scale
    // with n and with the regressors' units, so a fixed threshold would refuse a well-conditioned design
    // in small units and accept a singular one in large. A constant regressor — the k=1 case
    // NeweyWest.Ols rejects by its determinant — drives its pivot to exactly zero here and is refused.

    private static double[,] Cholesky(double[,] a, int p)
    {
        var l = new double[p, p];
        for (var i = 0; i < p; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var sum = a[i, j];
                for (var m = 0; m < j; m++) sum -= l[i, m] * l[j, m];

                if (i == j)
                {
                    var floor = 1e-12 * Math.Max(a[j, j], 1.0);
                    if (sum <= floor)
                    {
                        throw new ArgumentException(
                            $"Degenerate design at column {j} (pivot {sum:E3} ≤ {floor:E3}) — the coefficients " +
                            "are unidentified. A constant or collinear regressor is the usual cause.",
                            nameof(a));
                    }
                    l[i, j] = Math.Sqrt(sum);
                }
                else
                {
                    l[i, j] = sum / l[j, j];
                }
            }
        }
        return l;
    }

    /// <summary>Solve (LLᵀ)x = b by forward then back substitution.</summary>
    private static double[] CholSolve(double[,] l, double[] b, int p)
    {
        var yv = new double[p];
        for (var i = 0; i < p; i++)
        {
            var sum = b[i];
            for (var m = 0; m < i; m++) sum -= l[i, m] * yv[m];
            yv[i] = sum / l[i, i];
        }

        var x = new double[p];
        for (var i = p - 1; i >= 0; i--)
        {
            var sum = yv[i];
            for (var m = i + 1; m < p; m++) sum -= l[m, i] * x[m];
            x[i] = sum / l[i, i];
        }
        return x;
    }

    /// <summary>(LLᵀ)⁻¹, by solving against each unit vector.</summary>
    private static double[,] CholInverse(double[,] l, int p)
    {
        var inv = new double[p, p];
        var e = new double[p];
        for (var col = 0; col < p; col++)
        {
            Array.Clear(e);
            e[col] = 1.0;
            var x = CholSolve(l, e, p);
            for (var r = 0; r < p; r++) inv[r, col] = x[r];
        }
        return inv;
    }
}
