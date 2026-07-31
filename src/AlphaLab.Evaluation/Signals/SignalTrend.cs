using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Signals;

/// <summary>The per-signal trend verdict (§24.2). Never hand-read: it is resolved from the pinned
/// significance levels and rendered verbatim.</summary>
public static class TrendFlag
{
    public const string Stable = "stable";
    public const string Decaying = "decaying";
    public const string Gone = "gone";
    /// <summary>Not enough history to infer anything — honest absence, never a silent "stable".</summary>
    public const string Insufficient = "insufficient";
}

/// <summary>
/// The effective independent sample behind a trend verdict, and the degrees of freedom it implies.
///
/// D108 PROMOTED THIS FROM A PRINTED CAVEAT TO A LOAD-BEARING INPUT. Overlapping k-day returns are not
/// independent, so a window of <c>WindowSessions</c> holds only about <c>WindowSessions / Horizon</c>
/// independent observations. That count sets <c>df</c>, which sets the critical value, which decides
/// the flag — so it is arithmetic the verdict rests on rather than a caveat printed beside it. It is
/// STILL displayed (the D107 print-the-denominator discipline), but its status is input first.
///
/// THE TWO ARMS DIFFER BECAUSE THEY FIT DIFFERENT THINGS. The <c>gone</c> arm tests a MEAN
/// (<c>df = n − 1</c>); the <c>decaying</c> arm fits a SLOPE (<c>df = n − 2</c>, one more parameter
/// estimated). That asymmetry is exactly why the trend arm is the binding constraint that made D108
/// choose the 5-year window: the harder arm sets the requirement.
/// </summary>
/// <param name="WindowSessions">Trading sessions in the inference window (5y ≈ 1260 — D108).</param>
/// <param name="HorizonDays">The grade horizon k, which is also the NW lag.</param>
public readonly record struct EffectiveSample(int WindowSessions, int HorizonDays)
{
    /// <summary>n_eff = window ÷ horizon, floored — the count of NON-OVERLAPPING observations.</summary>
    public int Count => HorizonDays <= 0 ? 0 : WindowSessions / HorizonDays;

    /// <summary>Degrees of freedom for the LEVEL arm (a mean): n − 1.</summary>
    public double LevelDf => Count - 1;

    /// <summary>Degrees of freedom for the TREND arm (a slope): n − 2.</summary>
    public double TrendDf => Count - 2;

    /// <summary>Both arms need a positive df to be testable at all.</summary>
    public bool CanInfer => TrendDf > 0;
}

/// <summary>
/// Resolves the §24.2 trend flag from a rolling rank-IC series [D108].
///
/// The pinned constants are SIGNIFICANCE LEVELS (α), never critical values: the critical value is
/// <c>t_{1−α, df}</c> computed here, because df depends on the effective sample and therefore cannot be
/// authored in advance. Blind pinning stays legitimate for exactly this reason — a significance level
/// can be chosen without seeing data, whereas an IC magnitude cannot, which is why abandoning the
/// significance framing was rejected as self-defeating.
///
/// Both arms are ONE-SIDED, because both claims are directional: "significantly negative" and "not
/// significantly above zero".
/// </summary>
public static class SignalTrendInference
{
    /// <summary>A resolved verdict together with everything needed to audit it.</summary>
    /// <param name="Flag">stable | decaying | gone | insufficient.</param>
    /// <param name="MeanIc">The window's mean rank-IC.</param>
    /// <param name="StdError">Newey–West standard error of that mean (lag = horizon).</param>
    /// <param name="TStat">MeanIc / StdError, or null when the error is not positive.</param>
    /// <param name="Sample">The effective independent sample the verdict rests on.</param>
    /// <param name="LevelCritical">The one-sided critical value used by the <c>gone</c> arm.</param>
    /// <param name="TrendCritical">The one-sided critical value used by the <c>decaying</c> arm.</param>
    public readonly record struct Verdict(
        string Flag, double MeanIc, double StdError, double? TStat,
        EffectiveSample Sample, double? LevelCritical, double? TrendCritical);

    /// <summary>
    /// Infer the flag. <paramref name="icSeries"/> is the window's per-day rank-IC values (oldest
    /// first); <paramref name="horizonDays"/> is both the grade horizon and the NW lag.
    ///
    /// Order matters and is deliberate: <c>gone</c> is evaluated BEFORE <c>decaying</c>. A signal whose
    /// mean is not distinguishable from zero is gone whatever its slope is doing — reporting "decaying"
    /// for it would describe the trajectory of something that is already indistinguishable from noise.
    /// </summary>
    public static Verdict Infer(
        IReadOnlyList<double> icSeries, int horizonDays, int windowSessions, double goneAlpha, double decayAlpha)
    {
        ArgumentNullException.ThrowIfNull(icSeries);

        var sample = new EffectiveSample(windowSessions, horizonDays);
        if (icSeries.Count < 2 || !sample.CanInfer)
        {
            var mean0 = icSeries.Count > 0 ? icSeries.Average() : 0.0;
            return new Verdict(TrendFlag.Insufficient, mean0, 0.0, null, sample, null, null);
        }

        var mean = icSeries.Average();

        // NW long-run variance at lag = horizon (overlapping k-day returns are serially correlated BY
        // CONSTRUCTION), divided by the EFFECTIVE count — not by icSeries.Count, which would treat
        // overlapping observations as independent and shrink the error by ~√k.
        var lrv = NeweyWest.LongRunVariance(icSeries, horizonDays);
        var se = Math.Sqrt(lrv / Math.Max(1, sample.Count));

        var levelCritical = StudentT.OneSidedCritical(goneAlpha, sample.LevelDf);
        var trendCritical = StudentT.OneSidedCritical(decayAlpha, sample.TrendDf);

        if (se <= 0)
        {
            // No dispersion: a mean that is exactly zero is "gone"; a nonzero one has no error bar to
            // judge it against, so it is reported insufficient rather than certified stable.
            var degenerate = mean == 0.0 ? TrendFlag.Gone : TrendFlag.Insufficient;
            return new Verdict(degenerate, mean, 0.0, null, sample, levelCritical, trendCritical);
        }

        var t = mean / se;

        // GONE: the mean is not significantly ABOVE zero (one-sided, at the level arm's df).
        if (t <= levelCritical) return new Verdict(TrendFlag.Gone, mean, se, t, sample, levelCritical, trendCritical);

        // DECAYING: the slope of the IC series is significantly negative (at the trend arm's df).
        var slopeT = SlopeTStat(icSeries, horizonDays, sample.Count);
        if (slopeT is { } st && st <= -trendCritical)
        {
            return new Verdict(TrendFlag.Decaying, mean, se, t, sample, levelCritical, trendCritical);
        }

        return new Verdict(TrendFlag.Stable, mean, se, t, sample, levelCritical, trendCritical);
    }

    /// <summary>
    /// t-statistic of the OLS slope of the rank-IC series on time, with the NW-corrected residual
    /// variance and the effective (not nominal) sample in the denominator — the same correction the
    /// level arm applies, for the same reason.
    /// </summary>
    private static double? SlopeTStat(IReadOnlyList<double> y, int lag, int effectiveN)
    {
        var n = y.Count;
        if (n < 3 || effectiveN < 3) return null;

        double meanX = (n - 1) / 2.0;
        var meanY = y.Average();
        double sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = i - meanX;
            sxx += dx * dx;
            sxy += dx * (y[i] - meanY);
        }
        if (sxx <= 0) return null;

        var slope = sxy / sxx;
        var intercept = meanY - slope * meanX;
        var residuals = new double[n];
        for (var i = 0; i < n; i++) residuals[i] = y[i] - (intercept + slope * i);

        var residualLrv = NeweyWest.LongRunVariance(residuals, lag);
        if (residualLrv <= 0) return null;

        // Var(slope) = σ²_LR / Sxx, rescaled from the nominal to the effective sample: the overlapping
        // series carries n points but only ~effectiveN independent ones.
        var varSlope = residualLrv / sxx * ((double)n / effectiveN);
        return varSlope > 0 ? slope / Math.Sqrt(varSlope) : null;
    }
}
