using System.Globalization;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Providers;

/// <summary>The watermark the stored providers replay at. A singleton so the reproduce command can pin
/// one run's watermark for the whole DI graph without threading it through every call site.</summary>
public sealed record StoredHistoryOptions(string Watermark);

/// <summary>
/// <see cref="IMarketDataProvider"/> served from the STORE at a fixed watermark instead of from EODHD
/// (checkpoint 3.5.1, FR-25). This is what lets `reproduce-day` re-run a past session with no network
/// call, no API token, and no chance of a live feed's later revision leaking into a pinned re-run.
///
/// WHY THIS IS THE RIGHT SEAM. `bars` (D40) and `corporate_actions` (D76) are versioned append-only and
/// read at a watermark, so the data a past run SAW is still in the store, exactly as it saw it — a
/// later correction is a higher version that a pinned read cannot see. Replaying the fetch from those
/// reads therefore reconstructs Stage 1's inputs faithfully. Substituting the two provider interfaces
/// (rather than cutting a seam into DailyPipeline) means the orchestrator, Stage1Fetch and the FR-6
/// quality gate run UNMODIFIED under reproduce — the reproduction exercises the real pipeline, not a
/// test-shaped variant of it.
///
/// Re-ingesting these values is a no-op by construction: ingestion is value-diff-append, so identical
/// values append no version (the same property multi-day catch-up relies on for idempotency).
/// </summary>
public sealed class StoredMarketDataProvider(
    AlphaLabDbContext db,
    IBarReadService bars,
    ICorporateActionReadService actions,
    StoredHistoryOptions options) : IMarketDataProvider
{
    public Task<IReadOnlyList<EodBar>> GetEodAsync(string symbol, string from, string to, string asOf, CancellationToken ct = default)
    {
        var securityId = ResolveSecurityId(symbol);
        IReadOnlyList<EodBar> series = bars.GetSeries(securityId, from, to, options.Watermark)
            .Select(b => new EodBar(b.Date, b.Open, b.High, b.Low, b.Close, b.AdjClose, b.Volume))
            .ToList();
        return Task.FromResult(series);
    }

    public Task<IReadOnlyList<DividendEvent>> GetDividendsAsync(string symbol, string from, string asOf, CancellationToken ct = default)
    {
        var securityId = ResolveSecurityId(symbol);
        // CashPerShare is the UNADJUSTED cash (CorporateActionIngestion fails closed rather than storing
        // the adjusted value), and IngestDividends reads only UnadjustedValue while the gate's
        // reconciliation check reads only Type + dates. So both fields carry the stored amount: nothing
        // downstream of this replay can observe the difference.
        IReadOnlyList<DividendEvent> dividends = actions.GetActionsAsOf(securityId, options.Watermark)
            .Where(a => a.Type == "dividend" && a.ExDate is not null)
            .Where(a => string.CompareOrdinal(a.ExDate, from) >= 0)
            .Select(a => new DividendEvent(a.ExDate!, a.CashPerShare, a.CashPerShare))
            .ToList();
        return Task.FromResult(dividends);
    }

    public Task<IReadOnlyList<SplitEvent>> GetSplitsAsync(string symbol, string from, string asOf, CancellationToken ct = default)
    {
        var securityId = ResolveSecurityId(symbol);
        IReadOnlyList<SplitEvent> splits = actions.GetActionsAsOf(securityId, options.Watermark)
            .Where(a => a.Type == "split" && a.Ratio is not null)
            .Where(a => string.CompareOrdinal(a.EffectiveDate, from) >= 0)
            .Select(a => new SplitEvent(
                a.EffectiveDate,
                a.Ratio!.Value,
                a.Ratio.Value.ToString("0.000000", CultureInfo.InvariantCulture) + "/1.000000"))
            .ToList();
        return Task.FromResult(splits);
    }

    // Fail closed (rule 10): a symbol with no security row means the caller's view of the universe
    // disagrees with the store, and guessing an id would silently reproduce the WRONG security's day.
    private long ResolveSecurityId(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var row = db.Securities.FirstOrDefault(s => s.CurrentSymbol == symbol)
            ?? throw new InvalidOperationException(
                $"Stored replay cannot resolve symbol '{symbol}' to a security_id — no securities row has that " +
                "current_symbol. Reproducing a day against a store that does not contain its universe would " +
                "silently compare the wrong thing (fail closed).");
        return row.SecurityId;
    }
}

/// <summary>The regime proxy's stored counterpart (<see cref="StoredMarketDataProvider"/>'s rationale
/// applies verbatim). The proxy carries no corporate actions — it is an index EOD — so only bars are
/// served. Its security_id resolves from the same append-only `config` row the ingestion wrote.</summary>
public sealed class StoredRegimeProxyProvider(
    AlphaLabDbContext db,
    IBarReadService bars,
    StoredHistoryOptions options) : IRegimeProxyProvider
{
    public Task<IReadOnlyList<EodBar>> GetProxyBarsAsync(string from, string to, string asOf, CancellationToken ct = default)
    {
        var proxyId = ResolveProxySecurityId();
        if (proxyId is null)
        {
            // No proxy configured for this arena: the forward run had none either, so an empty series
            // reproduces it faithfully (the label simply does not compute — DailyPipeline logs that).
            return Task.FromResult<IReadOnlyList<EodBar>>([]);
        }

        IReadOnlyList<EodBar> series = bars.GetSeries(proxyId.Value, from, to, options.Watermark)
            .Select(b => new EodBar(b.Date, b.Open, b.High, b.Low, b.Close, b.AdjClose, b.Volume))
            .ToList();
        return Task.FromResult(series);
    }

    // Resolved AS-OF the pinned watermark (D96): a proxy re-pointing appended after the reproduced/
    // replayed run committed must be invisible to it, exactly like a later bar version.
    private long? ResolveProxySecurityId() =>
        new ConfigReadService(db).ResolveLongAsOf(RegimeProxyIngestion.ProxyConfigKey, options.Watermark);
}
