namespace AlphaLab.Evaluation.Metrics;

/// <summary>
/// Constants for the metrics service. <see cref="RiskFreePlaceholderAnnual"/> is the DELIBERATE
/// placeholder for the French risk-free series (hard rule 6), which has no provider yet (its D41
/// factor_returns ingestion is a later data-workstream phase). It is read in EXACTLY ONE place —
/// <see cref="StrategyMetrics.RiskFreeDaily"/> — never as a bare 0 in metric code.
///
/// This is NOT a bound CONFIG key (CONFIG_REFERENCE has no Metrics section; inventing one for a value
/// destined for removal would violate the "never invent a key" rule). When French RF ingestion lands,
/// this constant is replaced by a per-day series lookup and the read-model's "rf_placeholder" reason
/// tag (surfaced on any absolute alpha/Sharpe cell) is dropped.
///
/// Honesty note: the promotion-critical machinery — the paired MDE, the gate, the population percentile
/// (S3) — is computed on active-return DIFFERENCES, where a constant RF cancels exactly. RF therefore
/// never enters a verdict; only a displayed absolute alpha/Sharpe would move if the placeholder were
/// nonzero. See PROGRESS.md (the data-workstream item to replace this).
/// </summary>
public static class MetricsConstants
{
    /// <summary>Annualized risk-free rate placeholder (0 until the French RF series is ingested).</summary>
    public const double RiskFreePlaceholderAnnual = 0.0;

    /// <summary>Trading days per year — the annualization factor used throughout (D48 / §1.1).</summary>
    public const double TradingDaysPerYear = 252.0;
}
