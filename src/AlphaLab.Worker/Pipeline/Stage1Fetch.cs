using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Worker.Pipeline;

/// <summary>One member to fetch + gate on a trading day. <see cref="PriorActions"/> (resolved by the
/// orchestrator at the run's watermark, D76) gives the reconciliation check its context; the fetch itself
/// carries no DB handle. <see cref="LastStoredDate"/> is the newest bar already stored for this security
/// at the watermark (or null) — flags on dates at or before it are dropped, because those bars were gated
/// on the run that first observed them (no duplicate flags across runs).</summary>
public sealed record Stage1Target(
    long SecurityId,
    string Symbol,
    IReadOnlyList<CorporateActionRow> PriorActions,
    string? LastStoredDate);

/// <summary>The regime proxy's fetch target. It carries no dividends/splits (an index has none) and is
/// fetched through the proxy provider, not the market-data provider.</summary>
public sealed record ProxyTarget(long SecurityId, string Symbol, string? LastStoredDate);

/// <summary>Everything Stage 1 needs, assembled by the orchestrator from read-only DB queries BEFORE
/// Stage 1 runs. Stage 1 itself touches only the network (providers) and pure computation (the gate).</summary>
public sealed record Stage1Request(
    string AsOf,
    string From,
    string Watermark,
    string ObservedAt,
    IReadOnlyCollection<string> ExpectedDates,
    IReadOnlyList<Stage1Target> Securities,
    ProxyTarget? Proxy);

/// <summary>
/// Stage 1 of the D53 pipeline: FETCH and gate the day's data. It makes NETWORK calls and PURE
/// computations and writes NOTHING.
///
/// THE ZERO-WRITE GUARANTEE IS STRUCTURAL. This class takes no <c>AlphaLabDbContext</c> in its
/// constructor, so it CANNOT write — the guarantee is a type-level fact, not a discipline (FR-29: a
/// Stage-1 failure leaves literally zero rows, because no run row exists yet). FX-StagedPipeline pins the
/// runtime side of the same claim: a provider that hard-fails leaves every table's row count unchanged.
///
/// WHAT IT GATES (finding M). The FR-6 gate runs over the fetched delta merged with the member's prior
/// corporate-action feed, so the just-fetched bar is checked for NaN / non-positive prices, return
/// outliers, and the dividend/split reconciliation alarm. Flags on dates already stored (≤
/// <see cref="Stage1Target.LastStoredDate"/>) are dropped so a re-run does not re-emit yesterday's
/// warnings; the ONE-TIME gating of the never-gated 20-year backfill is a bounded follow-on recorded in
/// PROGRESS (it needs a gated-through marker rather than a daily full-history re-gate that would spam
/// duplicate flags).
///
/// WARNS RIDE ALONG. A warning does not stop the day — it is carried in <see cref="StagedDay"/> and
/// persisted by Stage 2 once the run_id exists (D77). Only a REJECT (fail closed, rule 10) aborts before
/// Stage 2 opens.
/// </summary>
public sealed class Stage1Fetch(
    IMarketDataProvider marketData,
    IRegimeProxyProvider regimeProxy,
    IDataQualityGate gate)
{
    public async Task<StagedDay> FetchAsync(Stage1Request request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var securities = new List<StagedSecurity>(request.Securities.Count);
        foreach (var target in request.Securities)
        {
            var bars = await marketData.GetEodAsync(target.Symbol, request.From, request.AsOf, request.AsOf, ct).ConfigureAwait(false);
            var dividends = await marketData.GetDividendsAsync(target.Symbol, request.From, request.AsOf, ct).ConfigureAwait(false);
            var splits = await marketData.GetSplitsAsync(target.Symbol, request.From, request.AsOf, ct).ConfigureAwait(false);

            var report = Gate(target.Symbol, bars, target.PriorActions, dividends, splits,
                request.ExpectedDates, target.LastStoredDate);
            securities.Add(new StagedSecurity(target.SecurityId, target.Symbol, bars, dividends, splits, report));
        }

        StagedSecurity? proxy = null;
        if (request.Proxy is { } p)
        {
            // The proxy is fetched through its own provider (an index EOD, GSPC.INDX), carries no
            // corporate actions, and is gated with an empty action feed.
            var bars = await regimeProxy.GetProxyBarsAsync(request.From, request.AsOf, request.AsOf, ct).ConfigureAwait(false);
            var report = Gate(p.Symbol, bars, [], [], [], request.ExpectedDates, p.LastStoredDate);
            proxy = new StagedSecurity(p.SecurityId, p.Symbol, bars, [], [], report);
        }

        return new StagedDay(request.AsOf, request.Watermark, securities, proxy);
    }

    // Run the pure FR-6 gate over the fetched series, then drop flags on already-gated dates so a re-run
    // never re-emits a prior day's warnings. The action feed the reconciliation check sees is the stored
    // feed (at the watermark) unioned with today's freshly-fetched dividends/splits.
    private QualityReport Gate(
        string symbol,
        IReadOnlyList<EodBar> bars,
        IReadOnlyList<CorporateActionRow> priorActions,
        IReadOnlyList<DividendEvent> dividends,
        IReadOnlyList<SplitEvent> splits,
        IReadOnlyCollection<string> expectedDates,
        string? lastStoredDate)
    {
        var actions = MergeActions(priorActions, dividends, splits);
        var report = gate.Evaluate(symbol, bars, actions, expectedDates);

        if (lastStoredDate is null) return report; // nothing gated before ⇒ every flag is genuinely new

        // Keep series-level flags (null date) and flags strictly after the last already-gated bar.
        var fresh = report.Flags
            .Where(f => f.Date is null || string.CompareOrdinal(f.Date, lastStoredDate) > 0)
            .ToList();
        return fresh.Count == report.Flags.Count ? report : new QualityReport(fresh);
    }

    // The gate's reconciliation check reads only Type + ex/effective date, so the fetched dividends/splits
    // are projected to that minimal shape and unioned with the stored feed (which is already CorporateActionRow).
    private static IReadOnlyList<CorporateActionRow> MergeActions(
        IReadOnlyList<CorporateActionRow> prior,
        IReadOnlyList<DividendEvent> dividends,
        IReadOnlyList<SplitEvent> splits)
    {
        if (dividends.Count == 0 && splits.Count == 0) return prior;

        var merged = new List<CorporateActionRow>(prior);
        foreach (var d in dividends)
        {
            merged.Add(new CorporateActionRow { Type = "dividend", ExDate = d.Date, EffectiveDate = d.Date });
        }
        foreach (var s in splits)
        {
            merged.Add(new CorporateActionRow { Type = "split", EffectiveDate = s.Date });
        }
        return merged;
    }
}
