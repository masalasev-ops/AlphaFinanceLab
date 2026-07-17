using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Funnel;

/// <summary>
/// The result of trying to fill one <see cref="PlannedOrder"/> at the next open. A closed hierarchy
/// so the caller cannot forget the rejected case — an order that produced neither a trade nor a
/// logged reason is the silent failure rule 10 exists to prevent.
/// </summary>
public abstract record FillResult
{
    private FillResult() { }

    /// <summary>Filled. <see cref="Trade"/> is ready to persist; <see cref="Clip"/> is non-null iff
    /// the participation cap bound and some intended quantity went unfilled (bound for
    /// capacity_rejections).</summary>
    public sealed record Filled(Trade Trade, CapacityClip? Clip) : FillResult;

    /// <summary>Not filled, with a logged reason. <see cref="Clip"/> is non-null when the cause was
    /// the cap allowing nothing — still a capacity fact to record.</summary>
    public sealed record Rejected(SecurityId SecurityId, string Reason, CapacityClip? Clip = null) : FillResult;
}

/// <summary>
/// Fills the orders a PRIOR session decided (Stage 6) at THIS session's open — the T+1 half of
/// "decide at close T, fill at next open T+1" (MASTER §6). PURE: it prices through the D43
/// <see cref="VirtualBroker"/> and hands back trades; the caller (the D53 pipeline, 2.10) persists
/// them inside the day's one transaction.
///
/// WHY THE ORDERS COME FROM A STORED SNAPSHOT, NOT A RECOMPUTE. The orders were built at close T
/// against T's watermark. This run reads at a LATER watermark, so recomputing the funnel now could
/// produce different orders if a correction to T's bars arrived overnight — the lab would fill
/// something other than what it decided, silently rewriting its own history. So the T+1 path reads
/// <see cref="DecisionSnapshot.Stage6Orders"/> back verbatim and fills exactly those. This class is
/// the reason the snapshot must round-trip exactly.
///
/// FORCED-EVENT FILLS DO NOT COME THROUGH HERE. A merger cash-out or delist force-exit is priced by
/// §13.6 (2.6/2.7) with standard costs waived and no participation cap — "a corporate action, not a
/// trade". Those trades are built directly against the ledger; only funnel-decided orders
/// (<see cref="TradeReason.Wishlist"/> / <see cref="TradeReason.ExitPolicy"/>) are filled here.
/// </summary>
public static class OrderFill
{
    /// <summary>
    /// Fill one order at its <see cref="PlannedOrder.FillOn"/> open.
    ///
    /// <paramref name="marketInputs"/> carries the T+1 open price and the ADV/σ statistics, resolved
    /// by the caller from the feature view at the FILL date's watermark. A missing input is a
    /// rejection with a reason (the broker fails closed) — never a defaulted fill.
    /// </summary>
    public static FillResult Fill(
        PlannedOrder order,
        MarketInputs marketInputs,
        long accountId,
        VirtualBroker broker,
        RunKind runKind)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(marketInputs);
        ArgumentNullException.ThrowIfNull(broker);

        if (order.Reason is not (TradeReason.Wishlist or TradeReason.ExitPolicy))
        {
            // Corporate-action and guardrail fills have their own priced paths (costs waived, no cap);
            // routing one through the broker here would wrongly charge it spread + impact.
            throw new ArgumentException(
                $"OrderFill handles only funnel-decided orders (Wishlist/ExitPolicy). '{order.Reason}' is a forced " +
                "event and is priced by its own §13.6 path, not the D43 broker.",
                nameof(order));
        }

        var outcome = broker.Execute(order.SecurityId, order.Side, order.Shares, marketInputs);

        return outcome switch
        {
            BrokerResult.Filled f => new FillResult.Filled(
                ToTrade(order, f, accountId, marketInputs, runKind),
                f.Clip),

            BrokerResult.Rejected r => new FillResult.Rejected(order.SecurityId, r.Reason, r.Clip),

            _ => throw new ArgumentOutOfRangeException(nameof(broker), outcome, "Unmapped broker result."),
        };
    }

    private static Trade ToTrade(
        PlannedOrder order, BrokerResult.Filled filled, long accountId, MarketInputs inputs, RunKind runKind)
    {
        // The broker already refused a fill without a positive price, so RawPrice is present here.
        var rawPrice = inputs.RawPrice
            ?? throw new InvalidOperationException(
                "A broker Filled result with no raw fill price is a contradiction — the broker rejects a missing price.");

        return new Trade
        {
            AccountId = accountId,
            SecurityId = order.SecurityId,
            Side = order.Side,
            DecidedOn = order.DecidedOn,
            FilledOn = order.FillOn,
            Shares = filled.Shares,               // the ALLOWED size — the cap already clipped it
            RawFillPrice = rawPrice,
            Commission = filled.Costs.Commission,
            SpreadCost = filled.Costs.SpreadCost,
            ImpactCost = filled.Costs.ImpactCost,
            CostModelVersion = filled.Costs.CostModelVersion,
            Reason = order.Reason,
            ActionId = null,                      // funnel orders are never forced events
            RunKind = runKind,
        };
    }
}
