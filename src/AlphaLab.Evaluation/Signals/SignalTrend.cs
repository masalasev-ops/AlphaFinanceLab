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
/// STILL displayed (finding 290's beside-the-flag requirement — a thin number must never read as a
/// thick one), but its status is input first.
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
    /// <summary>
    /// The MINIMUM effective sample at which a verdict may be emitted at all. Below it the flag is
    /// <see cref="TrendFlag.Insufficient"/> — explicitly not stable, not decaying, not gone.
    ///
    /// DERIVED, NOT CHOSEN — from the same leg of D108's argument that rejected the 1-year window, and
    /// it is the leg a t reference cannot repair. Note the identity: because the NW lag IS the horizon,
    ///
    ///     lag/T = horizon / window = 1 / n_eff
    ///
    /// so a bound on the NW estimator's reliability IS a bound on n_eff, exactly. D108 recorded both
    /// endpoints on that scale: lag/T = 0.25 (n_eff ≈ 4) is "far outside where NW is reliable", and
    /// lag/T = 0.05 (n_eff ≈ 20) is sound. The standard practical guidance sitting between them — a HAC
    /// bandwidth of at most about a TENTH of the sample — maps to <c>lag/T ≤ 0.10</c>, i.e.
    /// <c>n_eff ≥ 10</c>. The floor is that inversion, not a preference.
    ///
    /// THIS IS NOT A HYPOTHETICAL REGIME. A 5-year window is not full during the first years of the
    /// backfill, so n_eff RAMPS: at k = 63 the floor is not reached until ~630 sessions (~2.5 years) of
    /// history exist, and at k = 21 until ~210 (~10 months). Those ramp years are precisely the low-df
    /// case — at the floor, trend df = 8, where the ONE-SIDED t critical value still exceeds the normal
    /// one by ~13 % (1.860 against 1.645 at α = 0.05 — the tail the trend arm actually uses; finding
    /// 302). That is why <see cref="Numerics.StudentT"/> is computed exactly rather than approximated:
    /// the small-sample correction is load-bearing in the OPERATING range, not just at the extreme
    /// D108 rejected.
    /// </summary>
    public const int MinimumCount = 10;

    /// <summary>n_eff = window ÷ horizon, floored — the count of NON-OVERLAPPING observations.</summary>
    public int Count => HorizonDays <= 0 ? 0 : WindowSessions / HorizonDays;

    /// <summary>Degrees of freedom for the LEVEL arm (a mean): n − 1.</summary>
    public double LevelDf => Count - 1;

    /// <summary>Degrees of freedom for the TREND arm (a slope): n − 2.</summary>
    public double TrendDf => Count - 2;

    /// <summary>Whether a verdict may be emitted: the effective sample must reach
    /// <see cref="MinimumCount"/>. Below it the honest answer is "not yet", never a flag.</summary>
    public bool CanInfer => Count >= MinimumCount;
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
    /// <param name="MinDetectableIc">
    /// THE SMALLEST TRUE MEAN RANK-IC THIS TEST WOULD HAVE CAUGHT, at the pinned α and power
    /// (finding 305). Null when power is unpinned or the sample cannot support the claim.
    ///
    /// Without it, <c>gone</c> and "too thin to tell" are the same string to a reader — which is the
    /// exact confusion the effective-sample printing was added to prevent one level up. `gone` is a
    /// FAILURE TO REJECT, and a failure to reject is only informative next to the effect size the test
    /// had the power to find: a `gone` at 0.002 says the rule is dead, a `gone` at 0.060 says the
    /// instrument is blind. Same discipline as D89 publishing an MDE beside a gate refusal.
    /// </param>
    /// <param name="MinDetectableTrendPerYear">
    /// The counterpart for the <c>decaying</c> arm: the shallowest true decay, in rank-IC per YEAR,
    /// this test would have caught. Annualized exactly as <c>MdeCalculator</c> annualizes its alpha MDE.
    /// Published for the same reason as the level floor — <c>stable</c> is also a failure to reject,
    /// so "we found no decay" needs the decay it could have found beside it.
    /// </param>
    /// <param name="SlopeStdError">NW standard error of the fitted slope, per grade-day. Published so
    /// the two floors above are recomputable rather than merely asserted.</param>
    public readonly record struct Verdict(
        string Flag, double MeanIc, double StdError, double? TStat,
        EffectiveSample Sample, double? LevelCritical, double? TrendCritical,
        double? MinDetectableIc = null, double? MinDetectableTrendPerYear = null,
        double? SlopeStdError = null);

    /// <summary>
    /// Infer the flag. <paramref name="icSeries"/> is the window's per-day rank-IC values (oldest
    /// first); <paramref name="horizonDays"/> is both the grade horizon and the NW lag.
    ///
    /// Order matters and is deliberate: <c>gone</c> is evaluated BEFORE <c>decaying</c>. A signal whose
    /// mean is not distinguishable from zero is gone whatever its slope is doing — reporting "decaying"
    /// for it would describe the trajectory of something that is already indistinguishable from noise.
    /// </summary>
    /// <param name="power">
    /// The detection power the published floors are quoted at (<c>SignalLibrary.MinDetectablePower</c>,
    /// a versioned config row). NULL means unpinned ⇒ the floors are withheld rather than defaulted,
    /// the same fail-closed shape the α values use. It is NOT a verdict input: no flag depends on it,
    /// which is why an absent power withholds a DIAGNOSTIC and never a verdict, and why the FR-45
    /// backfill does not refuse to grade without it.
    /// </param>
    public static Verdict Infer(
        IReadOnlyList<double> icSeries, int horizonDays, int windowSessions, double goneAlpha, double decayAlpha,
        double? power = null)
    {
        ArgumentNullException.ThrowIfNull(icSeries);

        // Below the effective-sample floor there is NO verdict — not a provisional "stable". The
        // rolling mean is still reported (a reader may look at the number); what is withheld is the
        // significance CLAIM, which is the thing the sample cannot support.
        var sample = new EffectiveSample(windowSessions, horizonDays);
        if (icSeries.Count < 2 || !sample.CanInfer)
        {
            var mean0 = icSeries.Count > 0 ? icSeries.Average() : 0.0;
            return new Verdict(TrendFlag.Insufficient, mean0, 0.0, null, sample, null, null);
        }

        var mean = icSeries.Average();

        // NW long-run variance at lag = horizon, divided by the NOMINAL count (finding 306).
        //
        // THE DIVISOR IS T, NOT n_eff, AND THE DISTINCTION IS THE WHOLE POINT. σ²_LR is the LONG-RUN
        // variance: it is already γ₀ + 2Σw_k·γ_k, so the serial correlation induced by overlapping
        // k-day returns is ALREADY inside it. The textbook result is Var(ȳ) = σ²_LR / T with T nominal,
        // which is exactly what `MdeCalculator` does for the alpha MDE (σ_LR·252/√T). Dividing by n_eff
        // instead applies the overlap penalty a SECOND time and inflates every standard error by √k —
        // 7.9× at k=63. The "effective sample" shortcut is correct only against the RAW variance γ₀
        // (Var(ȳ) ≈ γ₀/n_eff); pairing it with σ²_LR double-counts.
        //
        // n_eff has NOT gone away — it still sets the df (below), because df measures how much
        // independent information constrains the variance ESTIMATE, which is a different question from
        // the variance of the mean. D108's df derivation is untouched by this fix.
        var lrv = NeweyWest.LongRunVariance(icSeries, horizonDays);
        var se = Math.Sqrt(lrv / Math.Max(1, icSeries.Count));

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

        // ---- the detectability floors (finding 305) ----
        // D48's convention, in rank-IC units instead of annualized alpha: MDE = (t_alpha + t_power)*se.
        // The POWER TERM is what makes this an MDE rather than a restatement of the critical value.
        // `levelCritical * se` would only be "the smallest mean that would have cleared the bar" — a
        // fact about this sample, already derivable from the row. The question a reader actually has
        // about a `gone` is "how big an edge could this test have FOUND", and that carries the power
        // term. The t reference (not z) for the same reason D108 chose it: at these df the normal is
        // wrong, and the power term is evaluated at the SAME df as the arm it belongs to.
        var slope = SlopeFit_(icSeries, horizonDays, sample.Count);
        double? minDetectableIc = null, minDetectableTrend = null;
        if (power is { } p && p > 0 && p < 1)
        {
            minDetectableIc = (levelCritical + StudentT.OneSidedCritical(1.0 - p, sample.LevelDf)) * se;
            if (slope is { StdError: > 0 } fit)
            {
                minDetectableTrend =
                    (trendCritical + StudentT.OneSidedCritical(1.0 - p, sample.TrendDf))
                    * fit.StdError * SessionsPerYear;
            }
        }

        Verdict Resolved(string flag) => new(
            flag, mean, se, t, sample, levelCritical, trendCritical,
            minDetectableIc, minDetectableTrend, slope?.StdError);

        // GONE: the mean is not significantly ABOVE zero (one-sided, at the level arm's df).
        if (t <= levelCritical) return Resolved(TrendFlag.Gone);

        // DECAYING: the slope of the IC series is significantly negative (at the trend arm's df).
        if (slope is { TStat: { } st } && st <= -trendCritical) return Resolved(TrendFlag.Decaying);

        return Resolved(TrendFlag.Stable);
    }

    /// <summary>Trading sessions per year — the annualization constant, matching `MetricsConstants`.</summary>
    private const double SessionsPerYear = 252.0;

    /// <summary>The fitted slope, its NW standard error, and the resulting t — all three, because the
    /// error is now published (finding 305) and not merely divided by.</summary>
    private readonly record struct SlopeFit(double Slope, double StdError, double TStat);

    /// <summary>
    /// t-statistic of the OLS slope of the rank-IC series on time, with the NW-corrected residual
    /// variance and the effective (not nominal) sample in the denominator — the same correction the
    /// level arm applies, for the same reason.
    /// </summary>
    private static SlopeFit? SlopeFit_(IReadOnlyList<double> y, int lag, int effectiveN)
    {
        var n = y.Count;
        // effectiveN still gates the fit — a slope resting on fewer than three INDEPENDENT observations
        // is not worth reporting — but it no longer scales the variance (finding 306).
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

        // Var(slope) = σ²_LR,resid / Sxx — the standard HAC sandwich for a simple OLS slope. NO
        // effective-sample rescaling: the residual long-run variance already carries the overlap
        // correction, so multiplying by n/effectiveN would inflate the slope error by k, the same
        // double-count as the level arm (finding 306).
        var varSlope = residualLrv / sxx;
        if (varSlope <= 0) return null;
        var seSlope = Math.Sqrt(varSlope);
        return new SlopeFit(slope, seSlope, slope / seSlope);
    }
}
