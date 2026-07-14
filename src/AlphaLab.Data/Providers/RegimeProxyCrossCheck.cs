using System.Globalization;

namespace AlphaLab.Data.Providers;

/// <summary>The outcome of validating the regime proxy against SPY.US (INTEGRATIONS §9). Pure data.</summary>
public sealed record RegimeProxyCrossCheckResult(bool Agreed, int Compared, string? Alarm)
{
    /// <summary>No overlapping dates to compare — vacuously agreed (the caller decides if that is a problem).</summary>
    public static readonly RegimeProxyCrossCheckResult NoOverlap = new(true, 0, null);
}

/// <summary>
/// Validates the regime proxy series by comparing its daily returns to SPY.US on the overlapping dates
/// (INTEGRATIONS §9 — SPY's daily tracking error vs the S&amp;P 500 is negligible for a trend/vol label,
/// so a divergent sample flags a bad proxy). Pure and deterministic — the live SPY.US fetch (via the EODHD
/// equity provider) and the rotating-sample selection are wired by the backfill CLI (1.10); the comparison
/// is what the FR-6-style tolerance alarm rests on. Returns are computed on the RAW close (price-to-price):
/// GSPC.INDX is a price index, so raw-close returns are the apples-to-apples series; the small ex-dividend
/// gap on SPY ex-dividend days stays well inside the tolerance (a stop-and-report seam if it ever nears it).
/// </summary>
public static class RegimeProxyCrossCheck
{
    /// <summary>Per-day return-difference tolerance (percent). SPY vs the S&amp;P 500 price index tracks to a
    /// few bps/day; a divergent proxy sample blows past 0.5%. Not a CONFIG key at launch (INTEGRATIONS §9
    /// names no threshold) — an internal constant + stop-and-report seam like the FR-6 reconciliation epsilon.</summary>
    public const double DefaultTolerancePct = 0.5;

    /// <summary>Compare proxy vs SPY raw-close daily returns on overlapping dates. Returns the FIRST date
    /// whose absolute return difference exceeds <paramref name="tolerancePct"/> as the alarm, else agreed.</summary>
    public static RegimeProxyCrossCheckResult Compare(
        IReadOnlyList<EodBar> proxy, IReadOnlyList<EodBar> spy, double tolerancePct = DefaultTolerancePct)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(spy);

        var proxyReturns = DailyReturns(proxy);
        var spyReturns = DailyReturns(spy);

        var compared = 0;
        foreach (var (date, p) in proxyReturns.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!spyReturns.TryGetValue(date, out var s)) continue;
            // Only compare returns spanning the IDENTICAL interval. If the two feeds' priced-date sets
            // diverge (a gap in one), the same into-date carries a 1-day return on one side and a
            // multi-day return on the other — not comparable, so skip rather than fabricate an alarm.
            if (!string.Equals(p.PrevDate, s.PrevDate, StringComparison.Ordinal)) continue;

            compared++;
            var diffPct = Math.Abs(p.Ret - s.Ret) * 100.0;
            if (diffPct > tolerancePct)
            {
                return new RegimeProxyCrossCheckResult(false, compared,
                    $"{date}: proxy return {p.Ret.ToString("P2", CultureInfo.InvariantCulture)} vs SPY " +
                    $"{s.Ret.ToString("P2", CultureInfo.InvariantCulture)} differ {diffPct.ToString("F2", CultureInfo.InvariantCulture)}% " +
                    $"> {tolerancePct.ToString(CultureInfo.InvariantCulture)}% tolerance.");
            }
        }

        return compared == 0 ? RegimeProxyCrossCheckResult.NoOverlap : new RegimeProxyCrossCheckResult(true, compared, null);
    }

    // Raw-close return keyed by the INTO date, carrying the previous priced date so a caller can confirm
    // two series' returns span the identical interval before comparing them.
    private static Dictionary<string, (string PrevDate, double Ret)> DailyReturns(IReadOnlyList<EodBar> bars)
    {
        var ordered = bars
            .Where(b => b.Close is > 0)
            .OrderBy(b => b.Date, StringComparer.Ordinal)
            .ToList();

        var returns = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        for (var i = 1; i < ordered.Count; i++)
        {
            returns[ordered[i].Date] = (ordered[i - 1].Date, ordered[i].Close!.Value / ordered[i - 1].Close!.Value - 1.0);
        }
        return returns;
    }
}
