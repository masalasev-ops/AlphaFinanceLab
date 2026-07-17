using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// What applying one corporate action does to the ledger. A closed hierarchy so the Data adapter
/// cannot forget a case, and so "a ticker change does nothing" is an explicit, tested outcome
/// rather than a silent fall-through.
/// </summary>
public abstract record CorporateActionEffect
{
    private CorporateActionEffect() { }

    /// <summary>A dividend: cash credited on the ex-date. The position is UNCHANGED — a dividend
    /// does not change a share count, only the cash balance.</summary>
    public sealed record DividendCredited(CashEvent Cash, decimal PerShare, double Shares) : CorporateActionEffect;

    /// <summary>A split: the position is restated (shares × ratio, total cost basis unchanged, so the
    /// per-share basis divides by the ratio). NO cash moves, NO trade is booked, and equity is
    /// unchanged — the market price the exchange quotes also divides by the ratio.</summary>
    public sealed record PositionRestated(Position Before, Position After, double Ratio) : CorporateActionEffect;

    /// <summary>A ticker change: NOTHING happens to the ledger (D39). The identity is the
    /// security_id, which is unchanged; the alias was already updated in ticker_history at ingestion.
    /// This case exists to make "zero phantom churn on a rename" a tested fact, not an assumption.</summary>
    public sealed record TickerRenamedNoLedgerEffect(SecurityId Id, string? NewSymbol) : CorporateActionEffect;
}

/// <summary>
/// The §13.6 corporate-action ledger — PART 1 (2.6): dividend, split, ticker change, and the
/// fail-closed freeze for an unmapped bar stoppage. Mergers, spin-offs, and delisting are PART 2
/// (2.7); this engine refuses them by name so a dormant feed turning on cannot be silently dropped.
///
/// PURE. Given a position and an action it returns the effect as data; the Data adapter persists it
/// inside the day's one transaction. This is the highest-risk logic in the phase, which is exactly
/// why it is arithmetic over a Position rather than an emergent property of the pipeline — every
/// case is a fixture pointed straight at this function.
///
/// WHY EACH RULE IS WHAT IT IS:
///  • A dividend credits CASH and leaves the share count alone. Crediting on the EX-date (not the
///    pay date) is D30: ex-date is when the position's market value drops, so that is when the
///    matching cash must appear or the equity curve would show a phantom overnight loss.
///  • A split multiplies shares by the ratio and leaves the TOTAL cost basis alone. That is the
///    whole invariant: equity before = shares × price = (shares × r) × (price ÷ r) = equity after,
///    and total basis is unchanged so realized P&L on a later sale is identical. Booking a split as
///    a trade would invent a spurious buy/sell and pay it a spread it never crossed.
///  • A ticker change does NOTHING here. The position keeps its security_id; only a display alias
///    moved. This is the payoff of D39 — the reason FB→META is not a delisting-plus-new-listing that
///    corrupts a held position — and it is worth a test precisely because "rename ⇒ churn" is the
///    classic symbol-keyed bug this design exists to prevent.
/// </summary>
public static class CorporateActionLedger
{
    /// <summary>
    /// Apply one action to a held <paramref name="position"/>.
    ///
    /// The caller has already established that the account HOLDS the security and that the action is
    /// effective on the day being processed — this function does not re-check those; it prices the
    /// effect of an action it is told applies.
    /// </summary>
    public static CorporateActionEffect Apply(Position position, CorporateAction action, RunKind runKind)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(action);

        if (position.SecurityId != action.SecurityId)
        {
            throw new ArgumentException(
                $"Action {action.ActionId} is for security {action.SecurityId} but was applied to a position in " +
                $"{position.SecurityId}. The caller matched the wrong position to the action.",
                nameof(action));
        }

        return action.Type switch
        {
            CorporateActionType.Dividend => Dividend(position, action, runKind),
            CorporateActionType.Split => Split(position, action),
            CorporateActionType.TickerChange => new CorporateActionEffect.TickerRenamedNoLedgerEffect(
                position.SecurityId, action.NewSymbol),

            // Part 2 (2.7). Named so a dormant feed (D49) turning on refuses loudly rather than
            // dropping a merger/spin-off/delist on the floor.
            CorporateActionType.MergerCash or CorporateActionType.MergerStock or CorporateActionType.MergerMixed
                => throw new NotSupportedException(
                    $"Merger handling (type {action.Type}, action {action.ActionId}) is §13.6 part 2 — checkpoint 2.7. " +
                    "The merger feeds are dormant at launch (D49), so this cannot occur on live data yet; refusing " +
                    "rather than mispricing a conversion or cash-out."),
            CorporateActionType.Spinoff => throw new NotSupportedException(
                $"Spin-off handling (action {action.ActionId}) is §13.6 part 2 — checkpoint 2.7. Dormant feed (D49)."),
            CorporateActionType.Delist => throw new NotSupportedException(
                $"Delist force-exit (action {action.ActionId}) is §13.6 part 2 — checkpoint 2.7. Dormant feed (D49)."),

            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Type, "Unmapped corporate-action type."),
        };
    }

    private static CorporateActionEffect Dividend(Position position, CorporateAction action, RunKind runKind)
    {
        if (action.CashPerShare is not { } perShare)
        {
            // Fail closed (rule 10): a dividend with no cash amount cannot be priced. Never assume zero
            // — a silently-skipped dividend understates the total return the whole §5.1 test rests on.
            throw new InvalidOperationException(
                $"Dividend action {action.ActionId} ({action.SecurityId}) has no cash-per-share — cannot credit it. " +
                "Refusing rather than crediting zero.");
        }
        if (perShare < 0m)
        {
            throw new InvalidOperationException(
                $"Dividend action {action.ActionId} has a negative cash-per-share ({perShare}) — a dividend pays out, " +
                "it does not claw back. This is a corrupt feed value, not a chargeable event.");
        }

        // shares as of the ex-date × unadjusted cash per share. Fractional shares are fine (D68/§5.1).
        var amount = (decimal)position.Shares * perShare;

        var cash = new CashEvent
        {
            AccountId = position.AccountId,
            SecurityId = position.SecurityId,
            AsOf = action.ExDate ?? action.EffectiveDate,   // ex-date (D30)
            Type = CashEventType.Dividend,
            Amount = amount,
            ActionId = action.ActionId,
            RunKind = runKind,
        };

        return new CorporateActionEffect.DividendCredited(cash, perShare, position.Shares);
    }

    private static CorporateActionEffect Split(Position position, CorporateAction action)
    {
        if (action.Ratio is not { } ratio || !double.IsFinite(ratio) || ratio <= 0)
        {
            // Fail closed: a non-positive or non-finite ratio would zero out or corrupt the share count.
            throw new InvalidOperationException(
                $"Split action {action.ActionId} ({action.SecurityId}) has no positive, finite ratio " +
                $"(got {action.Ratio?.ToString() ?? "null"}) — cannot restate the position.");
        }

        var after = position with { Shares = position.Shares * ratio };
        // CostBasis is deliberately carried unchanged: total basis is invariant under a split, and the
        // per-share basis (basis ÷ shares) therefore divides by the ratio on its own. OpenedOn and the
        // frozen flag ride along unchanged — a split is not a new position and does not resolve a freeze.

        return new CorporateActionEffect.PositionRestated(position, after, ratio);
    }

    /// <summary>
    /// The fail-closed freeze decision for an UNMAPPED bar stoppage (§13.6 / rule 10). A held name
    /// whose bars stop needs an event to explain it; without one, the position is unpriceable and
    /// MUST freeze rather than be carried at a stale price (which would be the silent misprice rule
    /// 10 forbids).
    ///
    /// Returns the freeze reason, or null to leave the position as-is.
    ///
    /// The decision is: HELD, no bar at the run's watermark today, and no TERMINAL action
    /// (merger / spin-off / delist) effective today to explain the stoppage. A dividend or split does
    /// not explain a missing bar — the name should still be trading — so those do not clear the freeze.
    ///
    /// STOP-AND-REPORT SEAM (recorded in PROGRESS): this freezes on a SINGLE missing session, not only
    /// on a persistent stoppage. For the liquid S&P 100 a benign one-day gap is rare, and fail-closed
    /// says freeze-then-let-an-operator-resolve beats carrying a stale price. If the sp500 widening's
    /// thinner tail produces benign single-day gaps, a `Data.StoppageFreezeSessions` threshold could
    /// relax it — but that is a new CONFIG key and a D-number, not something to invent here.
    /// </summary>
    public static string? StoppageFreezeReason(
        Position position, bool hasBarToday, bool hasTerminalActionToday, string asOf)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        if (hasBarToday) return null;                       // it is trading — nothing to freeze
        if (hasTerminalActionToday) return null;            // a terminal event explains it; §13.6 part 2 handles the exit
        if (position.Frozen) return null;                   // already frozen — do not re-freeze / churn the reason

        return $"held position has no bar on {asOf} and no corporate action explains the stoppage — " +
               "freezing at the last print (fail closed, §13.6/rule 10). Operator must resolve.";
    }
}
