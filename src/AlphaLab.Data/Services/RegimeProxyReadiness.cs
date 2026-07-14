namespace AlphaLab.Data.Services;

/// <summary>The regime proxy warm-up verdict at a point in time (FR-38/D73). Pure data.</summary>
public sealed record RegimeProxyReadinessResult(bool IsReady, int SessionsAvailable, int SessionsRequired, string? Reason);

/// <summary>
/// The fail-closed warm-up guard for the regime label (FR-38/D73, INTEGRATIONS §9). The PIT label (D50/FR-26,
/// Phase 2) needs the proxy's trailing 3-year daily distribution (the vol percentile) AND a 200-day SMA (the
/// trend) before the FIRST label — so it refuses (and logs its reason) until enough proxy history exists,
/// NEVER fabricating a label on a short series (which would silently mis-calibrate the D56 curves the whole
/// monitor trusts). Phase 1 delivers the guard; the Phase-2 label service consumes it.
/// </summary>
public interface IRegimeProxyReadiness
{
    /// <summary>Is there enough proxy history on/before <paramref name="asOf"/> to compute a regime label?
    /// Not-ready carries a logged reason (fail closed).</summary>
    RegimeProxyReadinessResult CheckReadiness(long proxySecurityId, string asOf);
}

public sealed class RegimeProxyReadiness(AlphaLabDbContext db, RegimeOptions options) : IRegimeProxyReadiness
{
    /// <summary>Canonical NYSE sessions per year for sizing the warm-up (the exact count comes from the
    /// calendar in Phase 2; ~252 is the standard sizing constant).</summary>
    private const int TradingDaysPerYear = 252;

    public RegimeProxyReadinessResult CheckReadiness(long proxySecurityId, string asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        // Warm-up is additive per INTEGRATIONS §9 / the FR-38 DoD: the 200-day SMA plus the 3-year vol
        // distribution => ~3.8 years of sessions before the first label.
        var required = options.TrendSmaDays + options.VolLookbackYears * TradingDaysPerYear;

        // Distinct proxy SESSIONS on/before asOf (bars are versioned, so many rows can share a date). Ordinal
        // ISO date compare in memory — no dependence on EF's string.Compare translation.
        var available = db.Bars
            .Where(b => b.SecurityId == proxySecurityId)
            .Select(b => b.Date)
            .AsEnumerable()
            .Where(d => string.CompareOrdinal(d, asOf) <= 0)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (available >= required)
        {
            return new RegimeProxyReadinessResult(true, available, required, null);
        }

        return new RegimeProxyReadinessResult(false, available, required,
            $"regime proxy warm-up not met: {available}/{required} sessions on/before {asOf} " +
            $"({options.TrendSmaDays}-day SMA + {options.VolLookbackYears}y vol distribution) — " +
            $"refusing to compute a regime label (fail closed).");
    }
}
