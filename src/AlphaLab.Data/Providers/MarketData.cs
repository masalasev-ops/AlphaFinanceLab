namespace AlphaLab.Data.Providers;

/// <summary>One raw daily bar as delivered by a market-data provider (EODHD /eod). Raw OHLCV plus
/// the split+dividend-adjusted close. The provider supplies NO adjusted OHL (INTEGRATIONS §1), so
/// adj_open/adj_high/adj_low are never populated from this record.</summary>
public sealed record EodBar(
    string Date,
    double? Open,
    double? High,
    double? Low,
    double? Close,
    double? AdjClose,
    long? Volume);

/// <summary>A single dividend event (EODHD /div). Ex-date = <see cref="Date"/>; both the adjusted
/// and unadjusted per-share cash are supplied (INTEGRATIONS §1).</summary>
public sealed record DividendEvent(string Date, decimal? Value, decimal? UnadjustedValue);

/// <summary>A single split event (EODHD /splits). <see cref="Ratio"/> is the parsed
/// new/old ratio from the "4.000000/1.000000" string field (INTEGRATIONS §1).</summary>
public sealed record SplitEvent(string Date, double Ratio, string RawRatio);

/// <summary>
/// Market-data provider seam (FR-1). EODHD is the launch provider (D35); Alpaca is a configured
/// fallback. Fetch methods hit the network; the static parse helpers on the concrete provider are
/// pure and unit-tested against captured payloads.
/// </summary>
public interface IMarketDataProvider
{
    // <paramref name="asOf"/> is the run's OBSERVATION date — the day WE saw this data. It is the raw
    // cache's partition key (INTEGRATIONS §9: provenance-by-observation-day), threaded through the
    // contract so archival never rests on a caller convention (P1R-4): the query bounds (from/to) and
    // the observation day are distinct, even when a backfill happens to pass to == asOf.

    /// <summary>Daily bars for a symbol over [from, to] (inclusive), completed sessions only; the raw
    /// payload is archived under <paramref name="asOf"/> (the observation day, not the query bound).</summary>
    Task<IReadOnlyList<EodBar>> GetEodAsync(string symbol, string from, string to, string asOf, CancellationToken ct = default);

    /// <summary>Dividend events for a symbol from a start date (FR-3); archived under <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<DividendEvent>> GetDividendsAsync(string symbol, string from, string asOf, CancellationToken ct = default);

    /// <summary>Split events for a symbol from a start date (FR-3); archived under <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<SplitEvent>> GetSplitsAsync(string symbol, string from, string asOf, CancellationToken ct = default);
}
