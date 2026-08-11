using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Funnel;

/// <summary>
/// Convert a STORED order's share magnitude into the units the book now uses, when a corporate action
/// restated that security between the decision at close T and the fill at open T+1 (D142).
///
/// THIS IS NOT A RECOMPUTE, and the distinction is the whole justification. <see cref="OrderFill"/>'s
/// doctrine is that T+1 fills the stored decision verbatim and never re-derives it at a later watermark —
/// a strategy must not rewrite its own history. That doctrine is about the DECISION: which name, which
/// side, how much VALUE. A split changes none of those. It changes the name of the UNIT the quantity is
/// written in, and the exchange divides the quoted price in the same breath. "100 sh at P" and
/// "100·r sh at P/r" are the same order. Refusing to convert does not preserve the decision — it
/// CORRUPTS it, by exactly the ratio.
///
/// A DELTA ORDER RESCALES BY THE SAME FACTOR, and that is provable rather than assumed. Stage 6 builds a
/// delta as <c>targetNotional / price − currentShares</c>. The book restates by <c>r</c>, the exchange
/// restates the quote by <c>1/r</c>, and <c>targetNotional</c> is MONEY, which a split leaves alone.
/// So <c>targetShares' = targetNotional / (P/r) = targetShares · r</c> and
/// <c>delta' = (targetShares − currentShares) · r = delta · r</c>. Notional is preserved on both legs.
/// Hence no <see cref="Ledger.TradeReason"/> switch: the same multiplication is correct for a close, a
/// trim and an open, and a switch would be dead code that silently mishandles a reason added later.
/// (<c>CorpAction</c> and <c>Guardrail</c> orders never reach this path — <see cref="OrderFill"/> refuses
/// them — which is what makes a uniform rule safe.)
///
/// NOTHING IS WRITTEN BACK. The result is a local copy; <c>decisions.stage_json</c> is written once per
/// day for TODAY's snapshot and never rewritten by the fill path. "Restate the order" reads like
/// "rewrite the order", and it must not: the prior session's row stays byte-for-byte what it was.
/// </summary>
public static class OrderRestatement
{
    /// <summary>
    /// Apply <paramref name="ratiosInApplicationOrder"/> to <paramref name="order"/>'s share magnitude.
    ///
    /// Ratios are folded LEFT in the order the ledger applied them, never pre-multiplied, so a
    /// whole-line close stays bit-identical to the book the applier produced by the same sequence of
    /// multiplications. Two splits in one window compound as <c>(shares · r₁) · r₂</c>, which is not in
    /// general the same double as <c>shares · (r₁ · r₂)</c>.
    ///
    /// An empty ratio list returns the SAME INSTANCE, so the overwhelmingly common no-action path is
    /// provably untouched rather than merely equal.
    /// </summary>
    public static PlannedOrder Restate(PlannedOrder order, IReadOnlyList<double> ratiosInApplicationOrder)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(ratiosInApplicationOrder);

        if (ratiosInApplicationOrder.Count == 0) return order;

        var shares = order.Shares;
        foreach (var ratio in ratiosInApplicationOrder)
        {
            if (!double.IsFinite(ratio) || ratio <= 0)
            {
                // Fail closed, mirroring the ledger's own refusal on the position side: a share count in
                // a unit we cannot name is worse than a stopped run (rule 10).
                throw new InvalidOperationException(
                    $"Cannot restate the order for security {order.SecurityId}: a corporate action on its " +
                    $"fill date carries a non-positive or non-finite ratio ({ratio}).");
            }

            shares *= ratio;
        }

        return order with
        {
            Shares = shares,
            Rationale = order.Rationale +
                $" [restated ×{string.Join("×", ratiosInApplicationOrder)} by a corporate action effective " +
                $"on {order.FillOn}: {order.Shares} → {shares} sh, same decision in the book's units (D142)]",
        };
    }
}
