using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Providers;

/// <summary>
/// The replay engine's SIMULATED day (D95): a mutable singleton the day-loop advances, so the whole DI
/// graph agrees on "today" without threading a date through every call site (the
/// <see cref="StoredHistoryOptions"/> pattern for the date axis). Starts before any real date so an
/// un-advanced graph fails closed (every read sees nothing) rather than fail open (seeing everything).
/// </summary>
public sealed class ReplaySimDay
{
    /// <summary>The current simulated session (ISO yyyy-MM-dd).</summary>
    public string Current { get; private set; } = "0001-01-01";

    /// <summary>Advance to <paramref name="simAsOf"/>. Monotonic: replay processes sessions in order,
    /// and a backwards jump would mean two generations interleaved (fail closed, rule 10).</summary>
    public void Advance(string simAsOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simAsOf);
        if (string.CompareOrdinal(simAsOf, Current) < 0)
        {
            throw new InvalidOperationException(
                $"ReplaySimDay cannot move backwards ({Current} -> {simAsOf}) — replay processes sessions in order (D95).");
        }
        Current = simAsOf;
    }
}

/// <summary>
/// The DATE axis of the D95 replay watermark contract. Replay resolves row VERSIONS at the frozen real
/// watermark W_replay (backfilled history has only one observed version, stamped at backfill time — an
/// emulated historical observed_at would return nothing, and fabricating one would be the finding-194
/// fiction reborn). What CAN be emulated honestly is the date axis: no action whose effective/ex date
/// lies after the simulated day is knowable. Bars are date-bounded by every caller already (feature
/// views and Stage 1 pass `to &lt;= asOf`); corporate-action reads were the one unbounded path — this
/// decorator bounds them, so DailyPipeline, CorporateActionApplier AND StoredMarketDataProvider (all of
/// which consume <see cref="ICorporateActionReadService"/>) run UNMODIFIED under replay and a
/// 2016-effective merger is invisible to a 2015 simulated day (FR19_ReplayDateCeiling test).
/// </summary>
public sealed class DateCeilingCorporateActionReads(
    ICorporateActionReadService inner,
    ReplaySimDay simDay) : ICorporateActionReadService
{
    public IReadOnlyList<CorporateActionRow> GetActionsAsOf(long securityId, string watermark) =>
        inner.GetActionsAsOf(securityId, watermark)
            .Where(a => string.CompareOrdinal(a.EffectiveDate, simDay.Current) <= 0
                        && (a.ExDate is null || string.CompareOrdinal(a.ExDate, simDay.Current) <= 0))
            .ToList();
}
