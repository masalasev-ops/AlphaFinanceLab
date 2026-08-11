namespace AlphaLab.Core.Ledger;

/// <summary>
/// The share arithmetic of posting a fill to a book. PURE, so the invariants below are falsifiable by a
/// four-line unit test rather than only through a 700-line orchestrator — which is the whole reason this
/// exists as a type instead of staying inline in <c>DailyPipeline.PostFill</c>.
/// </summary>
public static class PositionMath
{
    /// <summary>
    /// Below this, a share quantity is floating-point noise rather than an intent.
    ///
    /// ONE definition, deliberately. It bounds two things that must agree: the smallest delta Stage 6
    /// will route an order for (trading less would pay a spread to move nothing) and the remainder at
    /// which a sell is treated as having closed the line. If those two drifted apart, an order could be
    /// routed for a quantity the ledger then declined to recognise as a close. It lives on the Ledger
    /// side because that is where <see cref="Position"/> lives, and <c>OrderBuilder</c> (Funnel) reads it
    /// from here — Funnel already depends on Ledger, so this is the direction that does not invert.
    ///
    /// An ABSOLUTE share tolerance, not a relative one. That is a pre-existing property of this codebase
    /// and this type does not change it; stated because an absolute epsilon on a fractional-share book is
    /// worth knowing about rather than discovering.
    /// </summary>
    public const double ShareEpsilon = 1e-9;

    /// <summary>
    /// The position after selling <paramref name="soldShares"/> out of <paramref name="existing"/>.
    /// A remainder within <see cref="ShareEpsilon"/> of zero closes the line (shares set to 0, which the
    /// store treats as a row removal — positions is current state, not a log); anything larger reduces
    /// the basis proportionally via <see cref="BasisMath.ReduceForSale"/>.
    ///
    /// THE OVERSELL REFUSAL, and why it is a throw rather than a clamp. Before this guard existed, a
    /// NEGATIVE remainder fell into the same branch as a clean close: the row was deleted and the full,
    /// over-large proceeds were credited, so the ledger silently gained cash that never existed and no
    /// exception, flag or log line marked it. Clamping to zero would keep the fabricated cash and lose
    /// only the evidence. Rule 10's answer to an impossible book state is a stopped run, and this mirrors
    /// the sibling refusal one branch above it in the caller — a sell against a position that is not held
    /// at all.
    ///
    /// In normal operation it is UNREACHABLE: a corporate action that restates a position also restates
    /// that security's pending orders (D142), so by the time a fill is posted the two counts are already
    /// in the same share unit. It fires only when that pairing is broken, which is a bug in the pairing,
    /// not a market event to be absorbed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The sale exceeds the holding — an impossible close.
    /// </exception>
    public static Position ApplySell(Position existing, double soldShares)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var newShares = existing.Shares - soldShares;

        if (newShares < -ShareEpsilon)
        {
            throw new InvalidOperationException(
                $"Fill sells {soldShares} of security {existing.SecurityId} in account {existing.AccountId} " +
                $"but only {existing.Shares} are held — an OVERSELL of {-newShares} shares. The order was " +
                "decided against a book that has since been restated (a corporate action on the fill date), " +
                "or the ledger and the funnel disagree. Refusing to book an impossible close rather than " +
                "absorbing it as a clean exit and crediting proceeds that were never earned (rule 10).");
        }

        return newShares <= ShareEpsilon
            ? existing with { Shares = 0 }
            : existing with
            {
                Shares = newShares,
                CostBasis = BasisMath.ReduceForSale(existing.CostBasis, newShares, existing.Shares),
            };
    }
}
