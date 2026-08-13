namespace AlphaLab.Evaluation.Metrics;

/// <summary>
/// Constants for the metrics service.
///
/// **finding 445 — THIS COMMENT USED TO CLAIM SOMETHING NOTHING CHECKED, AND IT WAS FALSE.** It read:
/// *"It is read in EXACTLY ONE place — `StrategyMetrics.RiskFreeDaily` — never as a bare 0 in metric
/// code."* In fact <see cref="StrategyMetrics.RiskFreeDaily"/> had **zero production callers** (its only
/// caller anywhere was a unit test), while FIVE production sites passed a bare `0.0` directly:
/// `ReplayRegimeOutcomesWriter.cs`, `OverfittingMonitor.cs` ×2, `SeedingBacktestEngine.cs` ×2. The
/// sentence was true when written and quietly stopped being true; nothing could go red, so nothing did.
/// It is the D140 shape — a claim a line states without examining — sitting in the very comment that
/// scoped the work to replace it, and three independent research agents repeated it as fact. Recorded
/// here rather than merely deleted, because the interesting part is not the wrong sentence but that a
/// comment was doing a guard's job.
///
/// **WHAT REPLACED IT.** The RF series is now a real per-day lookup — <see cref="RiskFreeSeries"/> over
/// `factor_returns` (D41) — and the metric functions carry per-day overloads. The constant below is NOT
/// deleted, and its remaining job is honest and narrow: it is the rate used when an arena has **no RF
/// data at all**, which every arena is in until the monthly refresh first runs. That state is REPORTED
/// (`RiskFreeWindow.FullyCovered` is false ⇒ the read-model carries
/// <c>MetricCell.ReasonRfPlaceholder</c>) rather than assumed away — which is what rule 10's "nothing is
/// ever silently defaulted" asks for, and what the reason tag was declared for in Phase 3 and never
/// wired to until now.
///
/// **WHERE RF DOES AND DOES NOT MATTER, corrected.** The old note said RF "never enters a verdict"
/// because the promotion-critical machinery differences it away. That is right for the PAIRED MDE and
/// the gate's head-to-head, and wrong in general: `PairedEffect.cs` records that RF shifts Jensen's α
/// through β ≠ 1, and Sharpe does not difference RF away at all — it is an absolute excess-return
/// statistic, so a constant 0 biases it by the whole level of rates. Both feed S2 and S6, and the
/// measured consequences of moving off 0 are recorded with the checkpoint rather than argued here.
///
/// This is NOT a bound CONFIG key: CONFIG_REFERENCE has no Metrics section, and the configurable half of
/// this subject now lives under `FactorData` (D41), where the SERIES is configured rather than a scalar.
/// </summary>
public static class MetricsConstants
{
    /// <summary>The rate assumed for a day with NO RF observation. Zero, and reported as uncovered
    /// rather than treated as measured — see the type remarks and <see cref="RiskFreeWindow"/>.</summary>
    public const double RiskFreePlaceholderAnnual = 0.0;

    /// <summary>Trading days per year — the annualization factor used throughout (D48 / §1.1).</summary>
    public const double TradingDaysPerYear = 252.0;
}
