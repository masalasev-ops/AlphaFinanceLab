using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>Named sources for the regime proxy (CONFIG <c>Regime.ProxySource</c>, D73/FR-38).</summary>
public static class RegimeProxySource
{
    /// <summary>EODHD <c>GSPC.INDX</c> EOD — the launch primary (verified on the tier, INTEGRATIONS §9).</summary>
    public const string EodhdGspc = "eodhd_gspc";

    /// <summary>Self-built cap-weight index over the backfilled universe — the fallback (dormant at launch).</summary>
    public const string SelfBuiltCapWeight = "self_built_capweight";
}

/// <summary>
/// The market-level regime proxy feed (D73/FR-38, INTEGRATIONS §9): the cap-weight daily series on which
/// the PIT regime label (D50/FR-26, Phase 2) is computed. It sits on the calibration critical path, so it
/// is a named, validated, fallback-bearing feed like every other (Golden Rule 25). One method — fetch the
/// proxy's daily bars over a window — shaped as <see cref="EodBar"/> so it ingests through the same
/// versioned-bar path as the universe and cross-checks against SPY.US by returns.
/// </summary>
public interface IRegimeProxyProvider
{
    Task<IReadOnlyList<EodBar>> GetProxyBarsAsync(string from, string to, CancellationToken ct = default);
}

/// <summary>
/// PRIMARY regime proxy: EODHD <c>GSPC.INDX</c> EOD (<c>GET /eod/GSPC.INDX?from=&amp;to=&amp;period=d</c>) —
/// the S&amp;P 500 index symbol, verified served on the launch tier (INTEGRATIONS §9, 2026-07-13). The index
/// symbol is already fully qualified, so — unlike the equity provider — NO <c>.US</c> suffix is appended.
/// Fetch and parse are split (the static <see cref="EodhdMarketDataProvider.ParseEod"/> is the shared,
/// byte-real-fixture-tested parser) so this is exercised offline against the captured GSPC payload.
/// </summary>
public sealed class EodhdGspcRegimeProxyProvider(
    IResilientHttpClient http,
    EodhdOptions options,
    IRawCache? rawCache = null) : IRegimeProxyProvider
{
    /// <summary>The EODHD-native index symbol — used verbatim as the security's ticker (never normalized;
    /// SymbolNormalizer's dot→dash would corrupt it) and as the <c>/eod</c> path.</summary>
    public const string ProxySymbol = "GSPC.INDX";

    private readonly IRawCache _rawCache = rawCache ?? NullRawCache.Instance;

    public async Task<IReadOnlyList<EodBar>> GetProxyBarsAsync(string from, string to, CancellationToken ct = default)
    {
        var url = $"{options.BaseUrl}/eod/{ProxySymbol}?api_token={options.ApiToken}&fmt=json&from={from}&to={to}&period=d";
        var json = await http.GetStringAsync(url, RegimeProxySource.EodhdGspc, ct).ConfigureAwait(false);
        _rawCache.Save(RegimeProxySource.EodhdGspc, to, $"{ProxySymbol}.eod.json", json);
        return EodhdMarketDataProvider.ParseEod(json);
    }
}

/// <summary>
/// FALLBACK regime proxy (DORMANT at launch, D73/FR-38): a self-built cap-weight index over the backfilled
/// universe bars with as-of membership — the same machinery D68 builds for the (cap-weighted) EW benchmark,
/// which does not exist until Phase 3+. Built now so the flip to <c>Regime.ProxySource='self_built_capweight'</c>
/// (if the index EOD ever becomes unavailable) is a config change, not new code. <see cref="GetProxyBarsAsync"/>
/// fails loud rather than returning an empty series that would silently mis-calibrate the regime label.
/// </summary>
public sealed class SelfBuiltCapWeightRegimeProxyProvider : IRegimeProxyProvider
{
    public const string DormantReason =
        "SelfBuiltCapWeightRegimeProxyProvider is a dormant fallback (D73/FR-38): the cap-weight index over " +
        "the backfilled universe (as-of membership) reuses the D68 benchmark machinery (Phase 3+). At launch " +
        "the primary EODHD GSPC.INDX proxy is used (INTEGRATIONS §9, verified on the tier); activate this " +
        "fallback via Regime.ProxySource='self_built_capweight' only if the index EOD becomes unavailable.";

    public Task<IReadOnlyList<EodBar>> GetProxyBarsAsync(string from, string to, CancellationToken ct = default) =>
        throw new NotSupportedException(DormantReason);
}
