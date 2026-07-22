namespace AlphaLab.Evaluation.Replay;

/// <summary>One equity observation of a backtest track (as_of, equity as the D69 decimal).</summary>
public sealed record BacktestEquityPoint(string AsOf, decimal Equity);

/// <summary>What to backtest: one strategy over a window, optionally at a pinned watermark (null =
/// the frozen MAX(observed_at), the D95 default).</summary>
public sealed record BacktestRequest(
    string StrategyId, string From, string To, string? Watermark = null, bool Reset = false);

/// <summary>The seeding summary — S1's future "backtest reference" input. Metrics are DESCRIPTIVE
/// seeding numbers, never a promotion input (the engine writes no gate/monitor/allocator rows at all).</summary>
public sealed record BacktestResult(
    string StrategyId, string From, string To, string Watermark,
    IReadOnlyList<BacktestEquityPoint> Equity, double SeedingSharpe, double SeedingAlphaAnn);

/// <summary>
/// The walk-forward seeding engine (MASTER §9 component table): Arena Replay's RESTRICTED special
/// case — the same simulated-window machinery (frozen watermark, date ceiling, as-of membership,
/// run_kind='replay' quarantine) with the judging half AMPUTATED: no plants, and the evaluation
/// cadence never runs, so it writes no power_reports, no go_live_log, no overfitting rows, no
/// allocations — it NEVER judges promotions (FX-BacktestEngine-NeverPromotes pins it structurally).
/// Its output is a quarantined equity track + descriptive seeding metrics.
/// </summary>
public interface IBacktestEngine
{
    Task<BacktestResult> RunAsync(BacktestRequest request, CancellationToken ct = default);
}
