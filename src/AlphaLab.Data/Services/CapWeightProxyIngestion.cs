using System.Globalization;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// The cap-weight benchmark ETF proxy to ingest (STRATEGY_CATALOG §5.1 / D26/D27). The symbol + config key
/// are resolved by the CLI composition root from <c>AlphaLab.Strategies.CapWeightProxy</c> — AlphaLab.Data
/// cannot reference AlphaLab.Strategies, so they arrive here as plain strings. <see cref="Symbol"/> is the
/// BARE ticker (e.g. <c>OEF</c>), NOT the fully-qualified EODHD symbol (<c>OEF.US</c>): unlike the regime
/// proxy's dedicated no-suffix provider, the cap-weight proxy rides the ordinary member fetch path forward
/// (DailyPipeline's Stage-1 set), where <see cref="Providers.EodhdMarketDataProvider"/> appends the exchange
/// suffix. So the proxy must be REGISTERED with the bare ticker for its daily fetch URL to be correct.
/// </summary>
public sealed record CapWeightProxyTarget(string Symbol, string Exchange, string ConfigKey, string Source)
{
    /// <summary>Split a fully-qualified EODHD symbol (<c>"TICKER.EXCHANGE"</c>, e.g. <c>"OEF.US"</c>) into the
    /// bare ticker + exchange the member market-data path expects. Fail closed on a symbol that is not
    /// fully qualified rather than registering a proxy whose forward fetch URL would be malformed (rule 10).</summary>
    public static CapWeightProxyTarget FromEodhdSymbol(string eodhdSymbol, string configKey, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eodhdSymbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(configKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var dot = eodhdSymbol.LastIndexOf('.');
        if (dot <= 0 || dot == eodhdSymbol.Length - 1)
        {
            throw new ArgumentException(
                $"Expected a fully-qualified 'TICKER.EXCHANGE' cap-weight proxy symbol, got '{eodhdSymbol}'.");
        }
        return new CapWeightProxyTarget(eodhdSymbol[..dot], eodhdSymbol[(dot + 1)..], configKey, source);
    }
}

/// <summary>
/// Resolves the cap-weight benchmark proxy's identity and persists it as the versioned <c>config</c> row the
/// D53 pipeline reads (STRATEGY_CATALOG §5.1). The precedent is <see cref="RegimeProxyIngestion"/>, with two
/// deliberate deltas: (1) the proxy is a real ETF fetched through the ordinary <c>IMarketDataProvider</c>
/// member path — its bars + dividends + splits are ingested by <see cref="BackfillRunner.BackfillSecurityStep"/>,
/// not a bars-only proxy path — so this service only resolves identity + writes the pointer; (2) the symbol
/// and config key are passed in (they live on <c>AlphaLab.Strategies.CapWeightProxy</c>, which Data cannot
/// reference). The config row is append-only per D72/finding 108: a new version is written only when the
/// value changes, so a re-resolve is idempotent.
/// </summary>
public sealed class CapWeightProxyIngestion(AlphaLabDbContext db)
{
    /// <summary>Resolve (register if new) the proxy security for <paramref name="symbol"/>/<paramref name="exchange"/>
    /// and persist <paramref name="configKey"/> as a versioned config row holding its security_id. Returns the
    /// security_id. Idempotent — a re-resolve of the same id writes no new version.</summary>
    public long ResolveProxySecurityId(string symbol, string exchange, string configKey, string asOf, string reasonSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(configKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonSource);

        var id = ResolveOrRegister(symbol, exchange, asOf);

        // Persist the pointer as an append-only versioned config row — a new version only when the resolved
        // value changes (MAX(version)+1); a re-resolve of the same id writes nothing. Mirrors RegimeProxyIngestion.
        var current = db.Config
            .Where(c => c.Key == configKey)
            .AsEnumerable()
            .OrderByDescending(c => c.Version)
            .FirstOrDefault();

        var valueJson = id.ToString(CultureInfo.InvariantCulture);
        if (current is null || !string.Equals(current.ValueJson, valueJson, StringComparison.Ordinal))
        {
            db.Config.Add(new ConfigRow
            {
                Key = configKey,
                ValueJson = valueJson,
                Version = (current?.Version ?? 0) + 1,
                ChangedOn = asOf,
                Reason = $"resolved cap-weight proxy from Universe.Bootstrap.MembershipPrimary='{reasonSource}'"
            });
            db.SaveChanges();
        }

        return id;
    }

    // Resolve the proxy's identity by CURRENT symbol + exchange (asOf-INDEPENDENT), registering it once if
    // absent. Routing through SecurityMaster.ResolveOrRegister (the asOf-bounded ticker_history path) would
    // mint a DUPLICATE security — orphaning the already-backfilled bars — when a re-run carries an EARLIER
    // asOf than the first (asOf < valid_from resolves to null). An iShares flagship ETF (OEF/IVV) does not
    // rename in practice; if one ever did, CapWeightProxy.SymbolFor and this lookup would update together.
    // Register still opens a proper ticker_history alias, so a genuine future ticker change is representable
    // via SecurityMaster.RecordTickerChange on the SAME id.
    private long ResolveOrRegister(string symbol, string exchange, string firstSeen)
    {
        var existing = db.Securities.FirstOrDefault(s => s.CurrentSymbol == symbol && s.Exchange == exchange);
        return existing?.SecurityId ?? new SecurityMaster(db).Register(symbol, exchange, firstSeen);
    }
}
