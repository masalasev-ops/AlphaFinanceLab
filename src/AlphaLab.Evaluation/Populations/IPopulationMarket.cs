namespace AlphaLab.Evaluation.Populations;

/// <summary>
/// The per-day market inputs the population engine needs — the ONE shared per-family per-day compute
/// (§5.2 batching): the eligible list and each name's daily return are read once and reused across all
/// members. The engine never touches bars or the DB directly; the Worker backs this with a point-in-time
/// <c>BarFeatureView</c> at the watermark, tests back it with synthetic returns.
/// </summary>
public interface IPopulationMarket
{
    /// <summary>Securities eligible on <paramref name="date"/> (the index membership the funnel trades).</summary>
    IReadOnlyList<long> Eligible(string date);

    /// <summary>The security's simple daily return on <paramref name="date"/> (adj_close over the prior
    /// session). 0 when the name has no bar that day (a frozen/halted holding contributes no return).</summary>
    double DailyReturn(long securityId, string date);

    /// <summary>The D43 one-way cost of trading <paramref name="perNameNotional"/> of the security, as a
    /// FRACTION of that notional (half-spread + square-root impact). The engine multiplies by the member's
    /// equal weight. Only called for cost-on families.</summary>
    double OneWayCostFraction(long securityId, string date, decimal perNameNotional);

    /// <summary>The 0-based ordinal of <paramref name="date"/> in the arena's TRADING-SESSION sequence — the
    /// re-draw grid must step per SESSION, not per calendar day (STRATEGY_CATALOG §5.2: daily/banded/monthly
    /// are session-grid intervals). Flooring calendar days would make a "monthly" (21-session) family redraw
    /// every ~15 sessions. Deterministic (a pure function of the fixed arena calendar), so the population
    /// stays reconstructible.</summary>
    long SessionOrdinal(string date);
}
