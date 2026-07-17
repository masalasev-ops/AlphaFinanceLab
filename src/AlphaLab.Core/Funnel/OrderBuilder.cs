using System.Globalization;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Funnel;

/// <summary>
/// An order decided at close T, to be filled at the open of T+1. Serialized into
/// `decisions.stage_json` as Stage 6's content and read back by the T+1 run.
///
/// THERE IS NO `orders` TABLE, and that is a design decision, not an omission. An order here has no
/// lifecycle to track: it is decided at close T and fully resolved at open T+1 — filled, clipped
/// (the excess going to capacity_rejections), or rejected with a logged reason. Nothing is ever
/// working, partially filled, or carried to T+2. A table would hold write-once/read-once rows, and
/// would be a SECOND copy of a fact `stage_json` must record anyway (SCHEMA:207 defines it as the
/// "funnel stages 1-6 snapshot", and Stage 6 is orders) — and two copies of one fact drift.
/// </summary>
public sealed record PlannedOrder
{
    public required SecurityId SecurityId { get; init; }

    public required TradeSide Side { get; init; }

    /// <summary>A positive magnitude; <see cref="Side"/> carries the direction.</summary>
    public required double Shares { get; init; }

    public required TradeReason Reason { get; init; }

    /// <summary>ISO date of the close at which this was decided (T).</summary>
    public required string DecidedOn { get; init; }

    /// <summary>ISO date of the open at which it should fill (T+1).</summary>
    public required string FillOn { get; init; }

    /// <summary>Why, in words — carried into stage_json so the provenance survives without a
    /// reader having to re-derive it from the stage lists.</summary>
    public required string Rationale { get; init; }
}

/// <summary>
/// Stage 6 of the daily funnel (MASTER §6) — turn the plan into orders. PURE.
///
/// DECIDE AT CLOSE T, FILL AT NEXT OPEN T+1. The quantity is computed from T's RAW CLOSE (the last
/// price actually observable when the decision is made) and fills at T+1's RAW OPEN. The two differ
/// by the overnight gap, and the realized notional therefore misses its target slightly. That is
/// not an error to correct — it is the honest consequence of not being able to see tomorrow's open
/// tonight. Sizing off T+1's open instead would be a look-ahead that flatters every fill.
///
/// RAW PRICES, NEVER ADJUSTED (D30/§13.8). The ledger trades real share counts at the prices that
/// actually printed. Signals use adj_close; the two are never mixed within an account. Sizing off an
/// adjusted close would buy a share count that never existed at a price nobody paid — worth ~1.5%/yr
/// of phantom alpha if got wrong.
///
/// SHARES ARE FRACTIONAL, and that is load-bearing rather than lazy. §5.1's Buy&amp;Hold acceptance
/// requires total return to equal the proxy's total return minus one entry cost. With whole-share
/// rounding, the leftover cash stub would never be invested and B&amp;H's return would drift from the
/// proxy's by an amount that has nothing to do with the strategy — the criterion would need a
/// tolerance, and a tolerance is where a real bug hides. Fractional shares make it exactly
/// satisfiable. (A paper lab has no lot-size constraint to honour; `positions.shares` is REAL for
/// this reason and because splits produce genuine fractions.)
/// </summary>
public static class OrderBuilder
{
    /// <summary>
    /// Build T's orders.
    ///
    /// <paramref name="sizing"/> covers exactly <see cref="PortfolioPlan.ToSize"/>.
    /// <paramref name="held"/> is the current book — needed both to sell a close in full and to
    /// compute the DELTA on a rebalance (a name already at target trades nothing).
    /// <paramref name="rawCloseAt"/> resolves T's raw close for a security, or null when it has none.
    /// </summary>
    public static IReadOnlyList<PlannedOrder> Build(
        PortfolioPlan plan,
        SizingResult sizing,
        IReadOnlyList<Position> held,
        DateOnly asOf,
        DateOnly fillOn,
        Func<SecurityId, double?> rawCloseAt,
        ICollection<FunnelNote> notes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sizing);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(rawCloseAt);
        ArgumentNullException.ThrowIfNull(notes);

        if (fillOn <= asOf)
        {
            // The T+1 rule is the one thing this stage exists to enforce; a caller that passes a fill
            // date at or before the decision date is asking the lab to trade on a bar it could not
            // have acted on.
            throw new ArgumentOutOfRangeException(
                nameof(fillOn), fillOn,
                $"The fill date must be strictly after the decision date (decide at close {asOf:yyyy-MM-dd}, " +
                "fill at the NEXT open — MASTER §6). Filling on the decision date is look-ahead.");
        }

        var orders = new List<PlannedOrder>();
        var byId = held.ToDictionary(p => p.SecurityId, p => p);

        // ---- Closes: sell the whole position. ----
        foreach (var close in plan.Closes.OrderBy(c => c.Id.Value))
        {
            if (!byId.TryGetValue(close.Id, out var position))
            {
                // Planning a close for a name that isn't held means the caller's view of the book
                // disagrees with the planner's — a bug worth surfacing, not routing an order for.
                notes.Add(new FunnelNote(close.Id, "planned close skipped: the position is not in the book handed to Stage 6."));
                continue;
            }

            orders.Add(new PlannedOrder
            {
                SecurityId = close.Id,
                Side = TradeSide.Sell,
                Shares = position.Shares,
                Reason = close.Reason,
                DecidedOn = Iso(asOf),
                FillOn = Iso(fillOn),
                Rationale = close.Detail,
            });
        }

        // ---- Opens and rebalance trims: trade the DELTA to target. ----
        foreach (var target in sizing.Targets.OrderBy(t => t.Id.Value))
        {
            if (rawCloseAt(target.Id) is not { } price || !double.IsFinite(price) || price <= 0)
            {
                // Rule 10: no price, no order — with a reason. Never a guessed or stale price.
                notes.Add(new FunnelNote(target.Id,
                    $"no positive raw close on {asOf:yyyy-MM-dd} — cannot convert a target notional into a share " +
                    "count. No order (fail closed)."));
                continue;
            }

            var targetShares = (double)target.TargetNotional / price;
            var currentShares = byId.TryGetValue(target.Id, out var existing) ? existing.Shares : 0.0;
            var delta = targetShares - currentShares;

            if (Math.Abs(delta) < ShareEpsilon)
            {
                notes.Add(new FunnelNote(target.Id, "already at target — no order (trading zero shares is a cost with no effect)."));
                continue;
            }

            var opening = currentShares == 0.0;
            orders.Add(new PlannedOrder
            {
                SecurityId = target.Id,
                Side = delta > 0 ? TradeSide.Buy : TradeSide.Sell,
                Shares = Math.Abs(delta),
                // Both an open and a rebalance trim are the wish list acting: the ExitPolicy is the
                // only thing that CLOSES, and neither of these closes anything.
                Reason = TradeReason.Wishlist,
                DecidedOn = Iso(asOf),
                FillOn = Iso(fillOn),
                Rationale = opening
                    ? $"open to target {Money(target.TargetNotional)} at raw close {Num(price)} ({plan.Scope})."
                    : $"{(delta > 0 ? "add to" : "trim to")} target {Money(target.TargetNotional)} at raw close {Num(price)} ({plan.Scope}).",
            });
        }

        return orders;
    }

    /// <summary>Below this, a "delta" is floating-point noise rather than an intent. Trading it would
    /// pay a spread to move nothing.</summary>
    private const double ShareEpsilon = 1e-9;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string Money(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
