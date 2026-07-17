using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// The market facts a fill needs. Every one is nullable because every one can genuinely be absent
/// (a name with a short history has no 21-day ADV), and the broker's job is to REFUSE rather than
/// default. See <see cref="VirtualBroker"/>.
/// </summary>
public sealed record MarketInputs
{
    /// <summary>The raw (unadjusted) fill price — the next open (D30/MASTER §6).</summary>
    public decimal? RawPrice { get; init; }

    /// <summary>21-day ADV in shares. Drives the participation cap and the √(Q/ADV) ratio.</summary>
    public double? Adv21Shares { get; init; }

    /// <summary>21-day ADV in USD notional. Drives the spread bucket only.</summary>
    public double? Adv21Notional { get; init; }

    /// <summary>Realized daily volatility as a fraction (0.02 = 2%/day). The σ in k·σ·√(Q/ADV).</summary>
    public double? SigmaDaily { get; init; }
}

/// <summary>The costs of one fill, ready to stamp onto a <see cref="Trade"/>.</summary>
public sealed record FillCosts
{
    public required decimal Commission { get; init; }
    public required decimal SpreadCost { get; init; }
    public required decimal ImpactCost { get; init; }
    public required string CostModelVersion { get; init; }
    public required LiquidityBucket Bucket { get; init; }

    public decimal Total => Commission + SpreadCost + ImpactCost;
}

/// <summary>The participation cap bit: what was wanted vs what capacity allowed (D43). Destined
/// for capacity_rejections — the excess is surfaced, never silently dropped.</summary>
public sealed record CapacityClip
{
    public required double IntendedShares { get; init; }
    public required double AllowedShares { get; init; }
    public required double Adv21Shares { get; init; }

    public double RejectedShares => IntendedShares - AllowedShares;
}

/// <summary>
/// What the broker did with an order. A closed hierarchy so the caller cannot forget the
/// rejection case — an order that silently produced no fill and no reason is the failure mode
/// hard rule 10 exists to prevent.
/// </summary>
public abstract record BrokerResult
{
    private BrokerResult() { }

    /// <summary>Filled at <see cref="Shares"/> (which may be less than intended — see
    /// <see cref="Clip"/>). <see cref="Clip"/> is non-null iff the participation cap bound.</summary>
    public sealed record Filled(double Shares, FillCosts Costs, CapacityClip? Clip) : BrokerResult;

    /// <summary>No fill, with a logged reason. <see cref="Clip"/> is non-null when the cause was
    /// the cap allowing nothing at all — that still deserves a capacity_rejections row.</summary>
    public sealed record Rejected(string Reason, CapacityClip? Clip = null) : BrokerResult;
}

/// <summary>
/// Turns an intended quantity into a fill, priced by the D43 <see cref="CostModel"/>. PURE — no
/// DB, no clock; the caller persists whatever comes back.
///
/// COSTS ARE ALWAYS ON (hard rule 5). There is no "costs off" flag on this path. The cost-free
/// control population (D36, Phase 3) is display-only and gets its own construction; it must never
/// be reachable by passing a flag here, because a cost-free number that leaked into a forward
/// verdict would flatter every strategy.
///
/// FAIL CLOSED (hard rule 10 / F-CLOSED). Every market input is optional and every absence is a
/// REJECTION with a named reason — never a default. A missing ADV cannot become "assume infinite
/// liquidity", a missing σ cannot become "assume zero impact": both would silently price a fill
/// as free, which is the exact error the cost model exists to make impossible.
///
/// NOT THIS CLASS'S JOB: corporate actions. §13.6 waives standard costs on a merger cash-out or
/// conversion ("a corporate action, not a trade") and those fills are not capacity-capped either
/// — the event happens to you regardless of your size. The §13.6 ledger builds those trades
/// directly with zero costs; it does not route them through here.
/// </summary>
public sealed class VirtualBroker(CostModel costModel)
{
    /// <summary>
    /// Price and size one order. <paramref name="intendedShares"/> is a positive magnitude; the
    /// side is carried separately because cost is symmetric — crossing the spread and pushing the
    /// price cost the same whether you are entering or exiting.
    /// </summary>
    public BrokerResult Execute(SecurityId securityId, TradeSide side, double intendedShares, MarketInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (double.IsNaN(intendedShares) || double.IsInfinity(intendedShares))
        {
            return new BrokerResult.Rejected($"security {securityId}: intended share count is not a finite number.");
        }
        if (intendedShares <= 0)
        {
            return new BrokerResult.Rejected($"security {securityId}: intended share count must be > 0 (got {intendedShares}).");
        }

        // ---- Fail closed on every missing or nonsensical risk input (rule 10). ----
        if (inputs.RawPrice is not { } price || price <= 0)
        {
            return new BrokerResult.Rejected(
                $"security {securityId}: no positive raw fill price at the next open — cannot price a fill.");
        }
        if (inputs.Adv21Shares is not { } advShares || advShares <= 0 || double.IsNaN(advShares))
        {
            return new BrokerResult.Rejected(
                $"security {securityId}: no positive 21-day ADV (shares) — cannot apply the participation cap " +
                "or the √(Q/ADV) impact ratio. Refusing rather than assuming unlimited liquidity.");
        }
        if (inputs.Adv21Notional is not { } advNotional || advNotional <= 0 || double.IsNaN(advNotional))
        {
            return new BrokerResult.Rejected(
                $"security {securityId}: no positive 21-day ADV (notional) — cannot select the spread bucket. " +
                "Refusing rather than assuming the cheapest one.");
        }
        if (inputs.SigmaDaily is not { } sigma || sigma < 0 || double.IsNaN(sigma))
        {
            return new BrokerResult.Rejected(
                $"security {securityId}: no realized daily volatility — cannot compute k·σ·√(Q/ADV). " +
                "Refusing rather than assuming zero impact.");
        }

        // ---- Participation cap (D43): the EXCESS is rejected, not the order. ----
        var capShares = costModel.ParticipationCapShares(advShares);
        var allowedShares = Math.Min(intendedShares, capShares);
        var clip = allowedShares < intendedShares
            ? new CapacityClip
            {
                IntendedShares = intendedShares,
                AllowedShares = allowedShares,
                Adv21Shares = advShares,
            }
            : null;

        if (allowedShares <= 0)
        {
            // The cap allows literally nothing — the name is too illiquid for this book to trade.
            // Still a capacity fact, so the clip rides along to be logged.
            return new BrokerResult.Rejected(
                $"security {securityId}: the {costModel.ParticipationCapShares(advShares):G} share participation cap " +
                "allows no fill at all.",
                clip ?? new CapacityClip
                {
                    IntendedShares = intendedShares, AllowedShares = 0, Adv21Shares = advShares,
                });
        }

        // ---- Price it. Costs are computed on the ALLOWED size, not the intended one: you do not
        // pay impact on shares you never traded. ----
        var notional = price * (decimal)allowedShares;
        var bucket = costModel.Bucket(advNotional);

        var costs = new FillCosts
        {
            Commission = costModel.Commission(),
            SpreadCost = costModel.SpreadCost(notional, bucket),
            ImpactCost = costModel.ImpactCost(notional, allowedShares, advShares, sigma),
            CostModelVersion = costModel.ModelVersion,
            Bucket = bucket,
        };

        return new BrokerResult.Filled(allowedShares, costs, clip);
    }
}
