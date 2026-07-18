using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Worker.Pipeline;

/// <summary>
/// One security's staged fetch for a trading day: the just-fetched delta (bars + corporate actions) and
/// the FR-6 gate's verdict over the full series (the delta merged with the stored history — finding M,
/// so the never-gated backfill is checked too). Pure data — Stage 1 produced it without a single write.
/// </summary>
public sealed record StagedSecurity(
    long SecurityId,
    string Symbol,
    IReadOnlyList<EodBar> Bars,
    IReadOnlyList<DividendEvent> Dividends,
    IReadOnlyList<SplitEvent> Splits,
    QualityReport Report);

/// <summary>
/// The whole result of Stage 1 (fetch, ZERO writes): the day's staged securities plus the regime proxy,
/// each carrying its fetched delta and its gate report. The orchestrator decides what happens next:
///  • <see cref="HasRejects"/> ⇒ abort BEFORE the run row is even written (FR-29: a Stage-1 failure
///    leaves literally zero rows);
///  • otherwise Stage 2 opens, ingests the deltas, and persists every flag (warn AND reject) under the
///    now-existing run_id — D77, where the previously-discarded gate warnings finally land.
/// </summary>
public sealed record StagedDay(
    string AsOf,
    string Watermark,
    IReadOnlyList<StagedSecurity> Securities,
    StagedSecurity? Proxy)
{
    /// <summary>Every staged security including the proxy (the proxy's bars are gated + ingested too).</summary>
    public IEnumerable<StagedSecurity> All => Proxy is null ? Securities : Securities.Append(Proxy);

    /// <summary>Any fail-closed (reject) flag anywhere ⇒ the pipeline blocks Stage 2 (rule 10).</summary>
    public bool HasRejects => All.Any(s => s.Report.HasRejects);

    /// <summary>Total flags staged (warn + reject) — for the pipeline's log line.</summary>
    public int FlagCount => All.Sum(s => s.Report.Flags.Count);
}
