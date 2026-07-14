using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// Resolves the regime proxy's identity + ingests its bars (FR-38/D73, INTEGRATIONS §9). The proxy is
/// stored like any other security: a permanent <c>security_id</c> for GSPC.INDX (so <c>regime_labels.inputs_hash</c>
/// keys a real row in Phase 2) with its EOD as versioned append-only bars. The resolved id is persisted as
/// a versioned <c>config</c> row (Regime.ProxySecurityId) — append-only per D72/finding 108: a new version
/// is written only when the value changes, so a re-resolve is idempotent.
/// </summary>
public interface IRegimeProxyIngestion
{
    /// <summary>Resolve (register if new) the proxy security for <paramref name="source"/> and persist
    /// Regime.ProxySecurityId as a versioned config row. Returns the proxy's security_id. Idempotent.</summary>
    long ResolveProxySecurityId(string source, string asOf);

    /// <summary>Ingest the proxy's daily bars under <paramref name="proxySecurityId"/> (versioned append-only,
    /// source <c>eodhd_gspc</c>). Returns the number of new version rows written.</summary>
    int IngestProxyBars(long proxySecurityId, IReadOnlyList<EodBar> bars, string observedAt);
}

public sealed class RegimeProxyIngestion(AlphaLabDbContext db) : IRegimeProxyIngestion
{
    public const string ProxyConfigKey = "Regime.ProxySecurityId";

    public long ResolveProxySecurityId(string source, string asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        // Only the EODHD GSPC proxy is active at launch; the self-built cap-weight fallback is dormant (D73),
        // so an attempt to resolve it fails loud rather than silently registering a phantom security.
        var (symbol, exchange) = source switch
        {
            RegimeProxySource.EodhdGspc => (EodhdGspcRegimeProxyProvider.ProxySymbol, "INDX"),
            _ => throw new NotSupportedException(
                $"Unknown/inactive Regime.ProxySource '{source}'. Only '{RegimeProxySource.EodhdGspc}' is active " +
                $"at launch ('{RegimeProxySource.SelfBuiltCapWeight}' is a dormant fallback, D73).")
        };

        // The GSPC.INDX symbol is EODHD-native — registered verbatim (never through SymbolNormalizer).
        var id = ResolveOrRegisterSingleton(symbol, exchange, asOf);

        // Persist Regime.ProxySecurityId as an append-only versioned config row — a new version only when
        // the resolved value changes (MAX(version)+1); a re-resolve of the same id writes nothing.
        var current = db.Config
            .Where(c => c.Key == ProxyConfigKey)
            .AsEnumerable()
            .OrderByDescending(c => c.Version)
            .FirstOrDefault();

        var valueJson = id.ToString(CultureInfo.InvariantCulture);
        if (current is null || !string.Equals(current.ValueJson, valueJson, StringComparison.Ordinal))
        {
            db.Config.Add(new ConfigRow
            {
                Key = ProxyConfigKey,
                ValueJson = valueJson,
                Version = (current?.Version ?? 0) + 1,
                ChangedOn = asOf,
                Reason = $"resolved from Regime.ProxySource='{source}'"
            });
            db.SaveChanges();
        }

        return id;
    }

    // The proxy is a PERMANENT SINGLETON (an index symbol that never has a ticker change), so its identity
    // is resolved by symbol+exchange INDEPENDENTLY of asOf. Routing through SecurityMaster.ResolveOrRegister
    // (which resolves via the asOf-bounded ticker_history interval) would mint a DUPLICATE security when a
    // later resolve carries an EARLIER asOf — e.g. a catch-up/replay of a past session — silently orphaning
    // the bars already ingested under the first id (a fail-open path this closes).
    private long ResolveOrRegisterSingleton(string symbol, string exchange, string firstSeen)
    {
        var existing = db.Securities.FirstOrDefault(s => s.CurrentSymbol == symbol && s.Exchange == exchange);
        return existing?.SecurityId ?? new SecurityMaster(db).Register(symbol, exchange, firstSeen);
    }

    public int IngestProxyBars(long proxySecurityId, IReadOnlyList<EodBar> bars, string observedAt) =>
        new BarIngestionService(db).IngestEod(proxySecurityId, bars, observedAt, source: RegimeProxySource.EodhdGspc);
}
