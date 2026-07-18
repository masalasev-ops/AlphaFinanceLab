namespace AlphaLab.Data.Providers;

/// <summary>The EODHD endpoints whose requests count against the daily cap (INTEGRATIONS §1).</summary>
public enum EodhdEndpoint
{
    Eod,
    Div,
    Splits,
    News,
    BulkLastDay,
}

/// <summary>
/// Per-endpoint EODHD request cost against the 100,000/day cap (INTEGRATIONS §1). Call cost is NOT flat:
/// the single-symbol endpoints cost 1, <c>/news</c> costs 5, and <c>/eod-bulk-last-day</c> costs 100.
/// <c>api_usage_log</c> MUST weight by this or it silently under-reports consumption and could pass a
/// ≥50%-headroom check that should have failed.
///
/// The backfill uses ONLY cost-1 endpoints, so its 304-call total is numerically unchanged; the weight
/// seam ships now (checkpoint 2.12) so <c>/news</c> (Phase 5) and <c>/eod-bulk-last-day</c> fold into the
/// same accounting without re-deriving it. <c>eod-bulk-last-day</c> is DECLARED here but NOT wired to any
/// caller — at sp100 it costs 100 units vs 101 per-symbol (a wash); it only pays off after the sp500
/// widening. An unknown endpoint throws rather than assume a cost (fail closed, rule 10).
/// </summary>
public static class EodhdEndpointCost
{
    public const int Eod = 1;
    public const int Div = 1;
    public const int Splits = 1;
    public const int News = 5;
    public const int BulkLastDay = 100;

    public static int For(EodhdEndpoint endpoint) => endpoint switch
    {
        EodhdEndpoint.Eod => Eod,
        EodhdEndpoint.Div => Div,
        EodhdEndpoint.Splits => Splits,
        EodhdEndpoint.News => News,
        EodhdEndpoint.BulkLastDay => BulkLastDay,
        _ => throw new ArgumentOutOfRangeException(
            nameof(endpoint), endpoint, "Unknown EODHD endpoint — refusing to guess a request cost (fail closed)."),
    };
}
