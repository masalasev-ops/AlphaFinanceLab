namespace AlphaLab.Core.Config;

/// <summary>
/// The D43 cost model's coefficients (CONFIG_REFERENCE "Costs"). Every default here MIRRORS that
/// file — it is the single source of truth; never hard-code a value that belongs there.
///
/// Costs are ALWAYS ON (hard rule 5). Whether a strategy survives net of costs is the single most
/// consequential number in the system, and an unstated cost shape is an unfalsifiable one — which
/// is why every coefficient is config and <see cref="ModelVersion"/> stamps every fill.
///
/// Follows the …Options convention already used in AlphaLab.Data: SectionName + mutable get/set
/// defaults matching CONFIG.
/// </summary>
public sealed class CostsOptions
{
    public const string SectionName = "Costs";

    /// <summary>Stamped on every trade row (trades.cost_model_version). A cost-model change gets
    /// a new version so old fills stay attributable to the model that priced them.</summary>
    public string ModelVersion { get; set; } = "cm-1.0";

    public decimal CommissionPerTrade { get; set; } = 0.0m;

    /// <summary>Half-spread in basis points per liquidity bucket, keyed by 21-day ADV NOTIONAL.</summary>
    public HalfSpreadByBucket HalfSpreadBpByBucket { get; set; } = new();

    /// <summary>21-day ADV notional (USD) bucket thresholds.</summary>
    public BucketAdvThresholds BucketAdvUsdThresholds { get; set; } = new();

    /// <summary>The k in square-root impact k·σ_daily·√(Q/ADV).</summary>
    public double ImpactK { get; set; } = 0.1;

    public int AdvWindowDays { get; set; } = 21;

    /// <summary>Participation cap as a percent of 21-day ADV. Excess quantity is REJECTED, logged
    /// to capacity_rejections, and surfaced — capacity awareness made concrete.</summary>
    public double ParticipationCapPctAdv { get; set; } = 2.0;

    public sealed class HalfSpreadByBucket
    {
        public double Mega { get; set; } = 1.0;
        public double Large { get; set; } = 2.5;
        public double Other { get; set; } = 5.0;
    }

    public sealed class BucketAdvThresholds
    {
        /// <summary>≥ $400M/day 21d ADV notional.</summary>
        public double Mega { get; set; } = 4.0e8;

        /// <summary>≥ $100M/day 21d ADV notional.</summary>
        public double Large { get; set; } = 1.0e8;
    }
}
