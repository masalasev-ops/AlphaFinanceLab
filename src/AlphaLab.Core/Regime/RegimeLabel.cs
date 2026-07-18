namespace AlphaLab.Core.Regime;

/// <summary>The trend component of a regime label (D50/§20.1). Serialized to the DB tokens
/// <c>bull</c>/<c>bear</c> — the values the <c>ck_regime_labels_trend</c> CHECK enforces.</summary>
public enum RegimeTrend
{
    Bull,
    Bear
}

/// <summary>The volatility component of a regime label (D50/§20.1). Serialized to the DB tokens
/// <c>normal_vol</c>/<c>high_vol</c> — the values the <c>ck_regime_labels_vol</c> CHECK enforces.</summary>
public enum RegimeVol
{
    NormalVol,
    HighVol
}

/// <summary>
/// One point-in-time daily regime label: the cross product <c>trend × volatility</c> for a trading
/// session (D50/§20.1). <see cref="Label"/> is the denormalized cross product SCHEMA stores in
/// <c>regime_labels.label</c>; the two component tokens are what the CHECK constraints validate.
/// Pure data — no computation lives here.
/// </summary>
public sealed record RegimeLabelPoint(string Date, RegimeTrend Trend, RegimeVol Vol)
{
    /// <summary><c>bull</c> | <c>bear</c> — the ck_regime_labels_trend token.</summary>
    public string TrendToken => Trend == RegimeTrend.Bull ? "bull" : "bear";

    /// <summary><c>normal_vol</c> | <c>high_vol</c> — the ck_regime_labels_vol token.</summary>
    public string VolToken => Vol == RegimeVol.HighVol ? "high_vol" : "normal_vol";

    /// <summary>The denormalized cross product, e.g. <c>bull/high_vol</c> (regime_labels.label, D50).</summary>
    public string Label => $"{TrendToken}/{VolToken}";
}

/// <summary>A single proxy session handed to the labeler: the trading date and its adjusted close.
/// The labeler works on <c>adj_close</c> (the proxy's total-return series) per §20.1.</summary>
public sealed record ProxyClose(string Date, double AdjClose);

/// <summary>One entry of the trend trajectory (used for hysteresis testing and episode extraction).</summary>
public sealed record RegimeTrendPoint(string Date, RegimeTrend Trend);

/// <summary>
/// The D50/§20.1 label parameters, resolved from <c>Regime.*</c> config. Kept in Core (BCL only) and
/// window-based rather than year-based so the labeler is unit-testable with tiny warm-ups: the
/// <c>VolLookbackYears × 252</c> conversion happens in the Data factory, never here. A validating ctor
/// rejects nonsense up front (fail closed) rather than producing a silently-degenerate label.
/// </summary>
public sealed record RegimeLabelParams
{
    /// <summary>Trend SMA window in sessions (default 200). <c>bull</c> when adj_close &gt; this SMA.</summary>
    public int TrendSmaDays { get; }

    /// <summary>Hysteresis band as a PERCENT (default 1.0 ⇒ 1%). A flip needs the close beyond the SMA
    /// by ≥ this for <see cref="ConfirmDays"/> consecutive sessions.</summary>
    public double TrendHysteresisPct { get; }

    /// <summary>Consecutive sessions of confirmation a flip requires (default 5). Shared by trend and
    /// vol ("same 5-day confirmation", §20.1).</summary>
    public int ConfirmDays { get; }

    /// <summary>Realized-vol window in sessions (default 21). Reuses <see cref="Domain.PriceStatistics"/>:
    /// an N-session vol reads N+1 closes.</summary>
    public int VolWindowDays { get; }

    /// <summary>Percentile of the trailing vol distribution above which a session is <c>high_vol</c>
    /// (default 80).</summary>
    public int VolPercentile { get; }

    /// <summary>Length of the trailing vol distribution in SESSIONS (default 3y × 252 = 756). Sessions,
    /// not years, so a test can shrink the warm-up; the Data factory does the ×252.</summary>
    public int VolLookbackSessions { get; }

    public RegimeLabelParams(
        int trendSmaDays,
        double trendHysteresisPct,
        int confirmDays,
        int volWindowDays,
        int volPercentile,
        int volLookbackSessions)
    {
        if (trendSmaDays < 2)
            throw new ArgumentOutOfRangeException(nameof(trendSmaDays), trendSmaDays, "SMA window needs ≥ 2 sessions.");
        if (trendHysteresisPct < 0 || !double.IsFinite(trendHysteresisPct))
            throw new ArgumentOutOfRangeException(nameof(trendHysteresisPct), trendHysteresisPct, "Hysteresis band must be finite and ≥ 0.");
        if (confirmDays < 1)
            throw new ArgumentOutOfRangeException(nameof(confirmDays), confirmDays, "Confirmation needs ≥ 1 session.");
        if (volWindowDays < 2)
            throw new ArgumentOutOfRangeException(nameof(volWindowDays), volWindowDays, "Vol window needs ≥ 2 sessions (≥ 2 returns).");
        if (volPercentile is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(volPercentile), volPercentile, "Percentile must be in [0, 100].");
        if (volLookbackSessions < 1)
            throw new ArgumentOutOfRangeException(nameof(volLookbackSessions), volLookbackSessions, "Vol lookback needs ≥ 1 session.");

        TrendSmaDays = trendSmaDays;
        TrendHysteresisPct = trendHysteresisPct;
        ConfirmDays = confirmDays;
        VolWindowDays = volWindowDays;
        VolPercentile = volPercentile;
        VolLookbackSessions = volLookbackSessions;
    }

    /// <summary>The first session index at which BOTH components exist: the SMA needs
    /// <see cref="TrendSmaDays"/> closes, and the vol distribution needs <see cref="VolLookbackSessions"/>
    /// realized-vol observations (each of which needs <see cref="VolWindowDays"/>+1 closes).</summary>
    public int FirstLabelIndex => System.Math.Max(FirstTrendIndex, FirstVolIndex);

    /// <summary>First index where the SMA (and thus the trend) is computable.</summary>
    public int FirstTrendIndex => TrendSmaDays - 1;

    /// <summary>First index where the trailing vol distribution (and thus the vol label) is computable.</summary>
    public int FirstVolIndex => VolWindowDays + VolLookbackSessions - 1;
}
