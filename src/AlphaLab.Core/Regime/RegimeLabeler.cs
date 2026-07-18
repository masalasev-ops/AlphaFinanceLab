using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Regime;

/// <summary>
/// The pure D50/§20.1 regime labeler: the daily PIT label is the cross product <c>trend × volatility</c>,
/// computed from an index-proxy series (oldest-first, every date ≤ asOf, read at the run's watermark).
///
/// PURE, BCL only — no DB, no clock, no watermark. The Data <c>RegimeLabelService</c> supplies the
/// watermarked series and stamps the provenance hash; leak-freedom is therefore a property of WHAT it
/// is fed (a versioned read ≤ asOf), which is exactly why F-LEAK is a service-level test.
///
/// WHY IT RECOMPUTES THE WHOLE TRAJECTORY: the label is path-dependent — hysteresis holds yesterday's
/// label unless a flip is confirmed, so today's label depends on the entire history since warm-up. The
/// labeler derives that history by a forward pass over the proxy series it is given; it NEVER reads a
/// previously-persisted label (which was computed at a different watermark, and reading it would import
/// a forward run's state into a replay — a quarantine leak). Same watermark ⇒ byte-identical labels.
///
/// The realized-vol number is <see cref="PriceStatistics.RealizedVolDaily"/> — the SAME function D43's
/// impact term uses (finding N) — so D50's vol component cannot drift from D43's σ.
/// </summary>
public static class RegimeLabeler
{
    /// <summary>
    /// The full label trajectory from the first session where both components exist through the last
    /// session in <paramref name="series"/>. The label AT asOf is <c>Last()</c> (its <c>Date</c> is the
    /// last series date). Empty when the series is shorter than the warm-up — the service guards that
    /// with <c>IRegimeProxyReadiness</c> and fails closed, so an empty return is a defensive backstop.
    /// </summary>
    public static IReadOnlyList<RegimeLabelPoint> LabelSeries(IReadOnlyList<ProxyClose> series, RegimeLabelParams p)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(p);

        var trend = TrendTrajectory(series, p); // indexed by session, valid from p.FirstTrendIndex
        var vol = VolTrajectory(series, p);     // indexed by session, valid from p.FirstVolIndex

        var start = p.FirstLabelIndex;
        if (series.Count <= start) return [];

        var labels = new List<RegimeLabelPoint>(series.Count - start);
        for (var i = start; i < series.Count; i++)
        {
            labels.Add(new RegimeLabelPoint(series[i].Date, trend[i]!.Value, vol[i]!.Value));
        }
        return labels;
    }

    /// <summary>
    /// The trend trajectory alone (bull/bear per session from the first SMA-computable index), so the
    /// hysteresis behaviour is testable without the ~3.8-year vol warm-up. Seeded at the first index
    /// from the RAW comparison (adj_close vs SMA — a seed is not a flip), then hysteresis governs every
    /// flip thereafter.
    /// </summary>
    public static IReadOnlyList<RegimeTrendPoint> TrendSeries(IReadOnlyList<ProxyClose> series, RegimeLabelParams p)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(p);

        var trend = TrendTrajectory(series, p);
        var points = new List<RegimeTrendPoint>();
        for (var i = p.FirstTrendIndex; i < series.Count; i++)
        {
            points.Add(new RegimeTrendPoint(series[i].Date, trend[i]!.Value));
        }
        return points;
    }

    // ---- trend: adj_close vs 200d SMA with ±TrendHysteresisPct band, ConfirmDays confirmation ----
    private static RegimeTrend?[] TrendTrajectory(IReadOnlyList<ProxyClose> series, RegimeLabelParams p)
    {
        var n = series.Count;
        var closes = ValidatedCloses(series);
        var trend = new RegimeTrend?[n];

        var runAbove = 0; // consecutive sessions with close ≥ SMA·(1+band)
        var runBelow = 0; // consecutive sessions with close ≤ SMA·(1−band)

        for (var i = p.FirstTrendIndex; i < n; i++)
        {
            var sma = Mean(closes, i - (p.TrendSmaDays - 1), i);
            var band = p.TrendHysteresisPct / 100.0;
            var beyondAbove = closes[i] >= sma * (1.0 + band);
            var beyondBelow = closes[i] <= sma * (1.0 - band);
            runAbove = beyondAbove ? runAbove + 1 : 0;
            runBelow = beyondBelow ? runBelow + 1 : 0;

            if (i == p.FirstTrendIndex)
            {
                // Seed from the raw comparison — the first label is not a flip, so hysteresis does not apply.
                trend[i] = closes[i] > sma ? RegimeTrend.Bull : RegimeTrend.Bear;
                continue;
            }

            var prev = trend[i - 1]!.Value;
            trend[i] = (prev, runAbove, runBelow) switch
            {
                (RegimeTrend.Bear, var a, _) when a >= p.ConfirmDays => RegimeTrend.Bull,
                (RegimeTrend.Bull, _, var b) when b >= p.ConfirmDays => RegimeTrend.Bear,
                _ => prev // otherwise yesterday's trend holds (the whole point of hysteresis)
            };
        }
        return trend;
    }

    // ---- vol: 21d realized vol vs the VolPercentile of its trailing distribution, same confirmation ----
    private static RegimeVol?[] VolTrajectory(IReadOnlyList<ProxyClose> series, RegimeLabelParams p)
    {
        var n = series.Count;
        var closes = ValidatedCloses(series);
        var vol = new RegimeVol?[n];

        // Realized vol per session (null before the window fills). Reuses PriceStatistics (finding N):
        // an N-session vol reads N+1 closes, so rv[i] exists from i == VolWindowDays.
        var rv = new double?[n];
        for (var i = p.VolWindowDays; i < n; i++)
        {
            rv[i] = PriceStatistics.RealizedVolDaily(Slice(closes, i - p.VolWindowDays, i));
        }

        var runHigh = 0;   // consecutive sessions at/above the percentile threshold
        var runNormal = 0; // consecutive sessions below it

        for (var i = p.FirstVolIndex; i < n; i++)
        {
            // Trailing distribution: the last VolLookbackSessions realized-vol observations ending at i
            // (inclusive). At 756 points, whether "today" is inside its own trailing window is immaterial;
            // inclusive is pinned for reproducibility.
            var window = new List<double>(p.VolLookbackSessions);
            for (var j = i - p.VolLookbackSessions + 1; j <= i; j++) window.Add(rv[j]!.Value);
            var threshold = Percentile(window, p.VolPercentile);
            var high = rv[i]!.Value >= threshold;

            runHigh = high ? runHigh + 1 : 0;
            runNormal = high ? 0 : runNormal + 1;

            if (i == p.FirstVolIndex)
            {
                vol[i] = high ? RegimeVol.HighVol : RegimeVol.NormalVol; // seed from raw, no confirmation
                continue;
            }

            var prev = vol[i - 1]!.Value;
            vol[i] = (prev, runHigh, runNormal) switch
            {
                (RegimeVol.NormalVol, var h, _) when h >= p.ConfirmDays => RegimeVol.HighVol,
                (RegimeVol.HighVol, _, var l) when l >= p.ConfirmDays => RegimeVol.NormalVol,
                _ => prev
            };
        }
        return vol;
    }

    /// <summary>
    /// Linear-interpolation percentile (the PERCENTILE.INC / numpy-default "linear" method): rank =
    /// (p/100)·(N−1), interpolating between the two nearest order statistics. Deterministic and
    /// convention-pinned because it sets the high_vol threshold — a different percentile rule would
    /// silently shift the regime boundary.
    /// </summary>
    private static double Percentile(List<double> values, int percentile)
    {
        if (values.Count == 0) throw new ArgumentException("percentile of an empty distribution", nameof(values));
        values.Sort();
        if (values.Count == 1) return values[0];

        var rank = percentile / 100.0 * (values.Count - 1);
        var lo = (int)System.Math.Floor(rank);
        var hi = (int)System.Math.Ceiling(rank);
        if (lo == hi) return values[lo];
        var frac = rank - lo;
        return values[lo] + frac * (values[hi] - values[lo]);
    }

    private static double[] ValidatedCloses(IReadOnlyList<ProxyClose> series)
    {
        var closes = new double[series.Count];
        for (var i = 0; i < series.Count; i++)
        {
            var c = series[i].AdjClose;
            if (!double.IsFinite(c) || c <= 0)
                throw new ArgumentOutOfRangeException(nameof(series), $"proxy adj_close at {series[i].Date} is not finite and positive.");
            if (i > 0 && string.CompareOrdinal(series[i].Date, series[i - 1].Date) <= 0)
                throw new ArgumentException($"proxy series must be strictly ascending by date; {series[i - 1].Date} then {series[i].Date}.", nameof(series));
            closes[i] = c;
        }
        return closes;
    }

    private static double Mean(double[] closes, int from, int to)
    {
        double sum = 0;
        for (var i = from; i <= to; i++) sum += closes[i];
        return sum / (to - from + 1);
    }

    private static double[] Slice(double[] closes, int from, int to)
    {
        var slice = new double[to - from + 1];
        Array.Copy(closes, from, slice, 0, slice.Length);
        return slice;
    }
}
