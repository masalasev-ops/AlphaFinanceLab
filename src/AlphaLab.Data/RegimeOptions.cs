namespace AlphaLab.Data;

/// <summary>
/// Regime configuration (CONFIG_REFERENCE "Regime", D50/D73). Phase 1 (FR-38, decision #3) uses only the
/// proxy-source resolution + the warm-up sizing (<see cref="TrendSmaDays"/> + <see cref="VolLookbackYears"/>);
/// the trend×vol label parameters are carried for the Phase-2 label service (FR-26) that consumes this.
/// <see cref="ProxySecurityId"/> stays null in appsettings and is RESOLVED at Phase 1 into a versioned
/// <c>config</c> row (Regime.ProxySecurityId) — the DB row is the authoritative runtime value. Follows the
/// …Options convention (SectionName + mutable get/set defaults matching CONFIG).
/// </summary>
public sealed class RegimeOptions
{
    public const string SectionName = "Regime";

    /// <summary>Resolved at Phase 1 from <see cref="ProxySource"/> (null in appsettings; the live value is a
    /// versioned config row). The cap-weight proxy's <c>security_id</c>.</summary>
    public long? ProxySecurityId { get; set; }

    /// <summary><c>eodhd_gspc</c> (GSPC.INDX EOD, primary) | <c>self_built_capweight</c> (dormant fallback).
    /// Pinned to the S&amp;P 500 proxy even during the D70 S&amp;P 100 slice — regimes are market-level facts.</summary>
    public string ProxySource { get; set; } = "eodhd_gspc";

    public int TrendSmaDays { get; set; } = 200;
    public double TrendHysteresisPct { get; set; } = 1.0;
    public int TrendConfirmDays { get; set; } = 5;
    public int VolWindowDays { get; set; } = 21;
    public int VolPercentile { get; set; } = 80;
    public int VolLookbackYears { get; set; } = 3;
}
