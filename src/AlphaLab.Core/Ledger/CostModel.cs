using AlphaLab.Core.Config;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// Liquidity bucket for the half-spread (D43). Keyed by 21-day ADV **notional** — this is the one
/// place notional is the right unit, because a spread is a property of how much money trades in a
/// name per day, not how many shares.
/// </summary>
public enum LiquidityBucket
{
    /// <summary>21d ADV notional ≥ Costs.BucketAdvUsdThresholds.Mega (default $400M/day).</summary>
    Mega,

    /// <summary>≥ Costs.BucketAdvUsdThresholds.Large (default $100M/day).</summary>
    Large,

    /// <summary>Everything else — the widest assumed spread.</summary>
    Other,
}

/// <summary>
/// The D43 cost model. PURE: given the order size and the name's market statistics it returns
/// money. No I/O, no clock, no DB — so `FX-CostModel` runs offline and the whole model is
/// falsifiable arithmetic rather than an emergent property of the pipeline.
///
/// per-fill cost = commission + half-spread(bucket) + k·σ_daily·√(Q/ADV)
///
/// Why this is worth stating precisely: whether a strategy survives net of costs is the single
/// most consequential number in the system, and an unstated cost shape is an unfalsifiable one.
/// Every coefficient is config (CONFIG "Costs"), and <see cref="CostsOptions.ModelVersion"/>
/// stamps every fill so old trades stay attributable to the model that priced them.
///
/// UNITS — the seam D43 needs but never states. Q/ADV must be dimensionless for the square-root
/// law to mean anything, so the cap and the impact ratio are in **shares**; the spread bucket is
/// in **notional**. Mixing them (e.g. Q in shares over ADV in dollars) would silently produce a
/// tiny ratio and near-zero impact on every name — costs that look modelled but aren't.
/// </summary>
public sealed class CostModel(CostsOptions options)
{
    private const double BasisPointsPerUnit = 10_000.0;

    public string ModelVersion => options.ModelVersion;

    /// <summary>The bucket for a 21-day ADV notional (USD/day). Thresholds are inclusive lower
    /// bounds, mega first.</summary>
    public LiquidityBucket Bucket(double adv21Notional) =>
        adv21Notional >= options.BucketAdvUsdThresholds.Mega ? LiquidityBucket.Mega
        : adv21Notional >= options.BucketAdvUsdThresholds.Large ? LiquidityBucket.Large
        : LiquidityBucket.Other;

    /// <summary>Half-spread in basis points for a bucket.</summary>
    public double HalfSpreadBp(LiquidityBucket bucket) => bucket switch
    {
        LiquidityBucket.Mega => options.HalfSpreadBpByBucket.Mega,
        LiquidityBucket.Large => options.HalfSpreadBpByBucket.Large,
        LiquidityBucket.Other => options.HalfSpreadBpByBucket.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Unmapped liquidity bucket."),
    };

    /// <summary>The participation cap in SHARES: Q ≤ ParticipationCapPctAdv% of 21-day ADV shares.</summary>
    public double ParticipationCapShares(double adv21Shares) =>
        adv21Shares * options.ParticipationCapPctAdv / 100.0;

    /// <summary>Flat per-trade commission (default $0 — a modern retail assumption, and config so
    /// it is falsifiable rather than baked in).</summary>
    public decimal Commission() => options.CommissionPerTrade;

    /// <summary>Half-spread cost for a fill: notional × halfSpreadBp / 10,000. Paid on both sides
    /// — crossing the spread costs the same whether you are buying or selling.</summary>
    public decimal SpreadCost(decimal notional, LiquidityBucket bucket) =>
        notional * (decimal)(HalfSpreadBp(bucket) / BasisPointsPerUnit);

    /// <summary>
    /// The square-root impact FRACTION: k · σ_daily · √(Q/ADV). Dimensionless (a fraction of
    /// price), which is why Q and ADV must share the share unit.
    ///
    /// Square-root is the standard, defensible retail-scale default: impact grows sub-linearly in
    /// size, so doubling an order costs less than double the impact.
    /// </summary>
    public double ImpactFraction(double shares, double adv21Shares, double sigmaDaily)
    {
        if (adv21Shares <= 0) throw new ArgumentOutOfRangeException(nameof(adv21Shares), "ADV must be > 0.");
        return options.ImpactK * sigmaDaily * Math.Sqrt(Math.Abs(shares) / adv21Shares);
    }

    /// <summary>Impact cost in money: notional × the impact fraction. The fraction is a statistic
    /// (double); the product is money (decimal, D69).</summary>
    public decimal ImpactCost(decimal notional, double shares, double adv21Shares, double sigmaDaily) =>
        notional * (decimal)ImpactFraction(shares, adv21Shares, sigmaDaily);
}
