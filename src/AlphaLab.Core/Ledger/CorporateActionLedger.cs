using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// The extra facts a PART-2 action needs beyond the position and the action itself — supplied by the
/// Data adapter from the bar reader / the ledger (Core cannot read either). Part-1 kinds
/// (dividend/split/ticker) ignore this entirely; each part-2 kind validates only the fields it needs
/// and fails closed on an absence (rule 10).
/// </summary>
public sealed record CorporateActionContext
{
    /// <summary>The account's CURRENT position in the counterparty (the acquirer, for a stock/mixed
    /// merger), or null if it holds none yet. A stock merger MERGES into it — you can be converted
    /// into a name you already own — so the engine needs it to sum shares and carry basis correctly.</summary>
    public Position? ExistingCounterpartyPosition { get; init; }

    /// <summary>The last available RAW print for a delist force-exit. Required for a delist; a delist
    /// with no price cannot be exited (fail closed).</summary>
    public decimal? LastPrintPrice { get; init; }

    /// <summary>The bankruptcy haircut FRACTION in [0,1) for a delist (from
    /// CorporateActions.BankruptcyHaircutPct / 100). The exit price is last_print × (1 − haircut).</summary>
    public double? BankruptcyHaircut { get; init; }

    /// <summary>The spin-off receipt's share count, resolved by the adapter (parent shares × ratio, or
    /// a first-print fallback — see <see cref="SpinoffAllocation"/>).</summary>
    public double? SpinoffShares { get; init; }

    /// <summary>The cost basis to move from the parent to the spin-off, resolved by the adapter.</summary>
    public decimal? SpinoffBasisAllocated { get; init; }
}

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

    /// <summary>A cash merger / delist: the position is CLOSED by a forced sell at a given price with
    /// standard costs WAIVED (§13.6 — a corporate action, not a trade; not capacity-capped). The
    /// <see cref="Trade"/> realizes P&amp;L against the cost basis; the position is then removed.</summary>
    public sealed record PositionForceClosed(Trade Sell) : CorporateActionEffect;

    /// <summary>A stock merger: the target position is removed and the account is converted into
    /// <see cref="AcquirerAfter"/> at the exchange ratio, cost basis carried. No cash, no trade — a
    /// conversion realizes nothing.</summary>
    public sealed record StockMergerConverted(SecurityId TargetId, Position AcquirerAfter, double SharesConverted)
        : CorporateActionEffect;

    /// <summary>A mixed merger: the cash leg is credited (<see cref="Cash"/>) AND the stock leg is
    /// converted into <see cref="AcquirerAfter"/>, in one action. Full basis carries to the stock leg
    /// (see the note in <see cref="CorporateActionLedger"/>).</summary>
    public sealed record MixedMergerApplied(CashEvent Cash, SecurityId TargetId, Position AcquirerAfter, double SharesConverted)
        : CorporateActionEffect;

    /// <summary>A spin-off: the parent's basis is reduced (<see cref="ParentAfter"/>, shares unchanged)
    /// and a NEW <see cref="SpinoffPosition"/> is created, entering the account even if the spun-off
    /// name is not in the index (exit-only management thereafter).</summary>
    public sealed record SpinoffReceived(Position ParentAfter, Position SpinoffPosition) : CorporateActionEffect;
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
    public static CorporateActionEffect Apply(
        Position position, CorporateAction action, RunKind runKind, CorporateActionContext? context = null)
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

        // The context is optional at the call site; each part-2 handler validates only the fields it
        // actually needs and fails closed on an absence (a stock merger into an un-held acquirer needs
        // no context at all; a delist needs a last print; a spin-off needs its resolved terms).
        var ctx = context ?? new CorporateActionContext();

        return action.Type switch
        {
            CorporateActionType.Dividend => Dividend(position, action, runKind),
            CorporateActionType.Split => Split(position, action),
            CorporateActionType.TickerChange => new CorporateActionEffect.TickerRenamedNoLedgerEffect(
                position.SecurityId, action.NewSymbol),

            CorporateActionType.MergerCash => CashMerger(position, action, runKind),
            CorporateActionType.MergerStock => StockMerger(position, action, ctx),
            CorporateActionType.MergerMixed => MixedMerger(position, action, ctx, runKind),
            CorporateActionType.Spinoff => Spinoff(position, action, ctx),
            CorporateActionType.Delist => Delist(position, action, ctx, runKind),

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

    // ============================ Part 2 (2.7) — mergers, spin-off, delist ============================
    //
    // FORCED-EVENT COSTS ARE WAIVED (§13.6): a merger cash-out, a conversion, a delist force-exit — "a
    // corporate action, not a trade" — pay no commission / spread / impact and are not capacity-capped
    // (the event happens to you regardless of your size). So these build the Trade directly with zero
    // costs; they never route through the D43 VirtualBroker (which is why OrderFill refuses a CorpAction
    // order). reason = CorpAction and the action_id travel on every one, per §13.6's "logged with the
    // action id" and the LedgerStore invariant.

    private static CorporateActionEffect CashMerger(Position position, CorporateAction action, RunKind runKind)
    {
        var deal = RequirePositiveCash(action, "cash merger", "deal cash-per-share");
        // Close the whole position at the deal cash: a forced SELL that realizes P&L vs the basis.
        return new CorporateActionEffect.PositionForceClosed(
            ForcedSell(position, action, deal, runKind,
                $"cash merger: {position.Shares} sh × {deal} deal cash, costs waived (§13.6)."));
    }

    private static CorporateActionEffect StockMerger(Position position, CorporateAction action, CorporateActionContext ctx)
    {
        var (acquirerId, ratio) = RequireExchange(action, "stock merger");
        var acquirerAfter = ConvertToAcquirer(position, acquirerId, ratio, ctx, carriedBasis: position.CostBasis, action);
        return new CorporateActionEffect.StockMergerConverted(position.SecurityId, acquirerAfter, position.Shares * ratio);
    }

    private static CorporateActionEffect MixedMerger(
        Position position, CorporateAction action, CorporateActionContext ctx, RunKind runKind)
    {
        var cashPerShare = RequirePositiveCash(action, "mixed merger", "cash leg per share");
        var (acquirerId, ratio) = RequireExchange(action, "mixed merger");

        // SIMPLIFICATION (documented stop-and-report seam): the FULL target basis carries to the stock
        // leg and the cash leg is credited as a distribution, rather than allocating basis between the
        // two. §13.6 says "cost basis carries" for the stock portion; total value is conserved at the
        // event either way (cash + new position value = old position value), and only the split between
        // realize-now and realize-later differs. A tax-grade basis allocation is a nicety a paper
        // research lab does not need, and inventing one the design does not specify would be worse.
        var cash = new CashEvent
        {
            AccountId = position.AccountId,
            SecurityId = position.SecurityId,
            AsOf = action.EffectiveDate,
            Type = CashEventType.MergerCash,
            Amount = (decimal)position.Shares * cashPerShare,
            ActionId = action.ActionId,
            RunKind = runKind,
        };
        var acquirerAfter = ConvertToAcquirer(position, acquirerId, ratio, ctx, carriedBasis: position.CostBasis, action);
        return new CorporateActionEffect.MixedMergerApplied(cash, position.SecurityId, acquirerAfter, position.Shares * ratio);
    }

    private static CorporateActionEffect Spinoff(Position parent, CorporateAction action, CorporateActionContext ctx)
    {
        if (ctx.SpinoffShares is not { } spinoffShares || spinoffShares <= 0 || !double.IsFinite(spinoffShares))
        {
            throw new InvalidOperationException(
                $"Spin-off action {action.ActionId} has no positive spin-off share count in its context — the adapter " +
                "must resolve it (parent shares × ratio, or the first-print fallback) before this runs.");
        }
        if (ctx.SpinoffBasisAllocated is not { } basisToSpinoff || basisToSpinoff < 0m || basisToSpinoff > parent.CostBasis)
        {
            throw new InvalidOperationException(
                $"Spin-off action {action.ActionId} has an out-of-range allocated basis ({ctx.SpinoffBasisAllocated}) — " +
                $"it must be in [0, parent basis {parent.CostBasis}]. Basis is conserved: what the spin-off gains, the " +
                "parent loses.");
        }
        if (action.CounterpartySecurityId is not { } spinoffId)
        {
            throw new InvalidOperationException(
                $"Spin-off action {action.ActionId} has no counterparty security_id — there is no spun-off entity to receive.");
        }

        // The parent KEEPS its shares; only its basis drops by what moved to the spin-off (basis is
        // conserved). The spin-off is a NEW position that enters even if not in-index (exit-only).
        var parentAfter = parent with { CostBasis = parent.CostBasis - basisToSpinoff };
        var spinoffPosition = new Position
        {
            AccountId = parent.AccountId,
            SecurityId = spinoffId,
            Shares = spinoffShares,
            CostBasis = basisToSpinoff,
            OpenedOn = action.EffectiveDate,
        };
        return new CorporateActionEffect.SpinoffReceived(parentAfter, spinoffPosition);
    }

    private static CorporateActionEffect Delist(
        Position position, CorporateAction action, CorporateActionContext ctx, RunKind runKind)
    {
        if (ctx.LastPrintPrice is not { } lastPrint || lastPrint < 0m)
        {
            throw new InvalidOperationException(
                $"Delist action {action.ActionId} has no non-negative last-print price in its context — cannot force-exit " +
                "(fail closed). A delist with no price to sell into is exactly the unmapped case that freezes instead.");
        }
        var haircut = ctx.BankruptcyHaircut ?? 0.0;
        if (haircut < 0.0 || haircut >= 1.0 || !double.IsFinite(haircut))
        {
            throw new InvalidOperationException(
                $"Delist action {action.ActionId} has an out-of-range bankruptcy haircut ({haircut}); it must be in [0,1).");
        }

        // Force-exit at last print, less the bankruptcy haircut. haircut=0 → last print; haircut→1 → a
        // near-total loss; haircut=1 would be exactly zero, disallowed above so the "no price at all"
        // case stays the freeze path rather than a $0 sale.
        var exitPrice = lastPrint * (decimal)(1.0 - haircut);
        return new CorporateActionEffect.PositionForceClosed(
            ForcedSell(position, action, exitPrice, runKind,
                $"delist force-exit: {position.Shares} sh × {exitPrice} (last print {lastPrint}, haircut {haircut:P0}), " +
                "costs waived (§13.6)."));
    }

    // ---- part-2 helpers ----

    /// <summary>Build the acquirer position after a stock/mixed merger: existing holding (if any) plus
    /// the converted shares and the carried basis. You can be merged into a name you already own, so
    /// the shares and basis SUM.</summary>
    private static Position ConvertToAcquirer(
        Position target, SecurityId acquirerId, double ratio, CorporateActionContext ctx, decimal carriedBasis, CorporateAction action)
    {
        var convertedShares = target.Shares * ratio;
        var existing = ctx.ExistingCounterpartyPosition;

        if (existing is not null && existing.SecurityId != acquirerId)
        {
            throw new InvalidOperationException(
                $"Merger action {action.ActionId}: the supplied existing counterparty position is in " +
                $"{existing.SecurityId}, but the acquirer is {acquirerId}. The adapter matched the wrong position.");
        }

        return new Position
        {
            AccountId = target.AccountId,
            SecurityId = acquirerId,
            Shares = (existing?.Shares ?? 0.0) + convertedShares,
            CostBasis = (existing?.CostBasis ?? 0m) + carriedBasis,
            OpenedOn = existing?.OpenedOn ?? action.EffectiveDate,
            Frozen = existing?.Frozen ?? false,
            FrozenReason = existing?.FrozenReason,
        };
    }

    /// <summary>A forced sell with standard costs WAIVED (§13.6): commission/spread/impact all zero,
    /// reason CorpAction, carrying the action_id.</summary>
    private static Trade ForcedSell(Position position, CorporateAction action, decimal price, RunKind runKind, string _)
        => new()
        {
            AccountId = position.AccountId,
            SecurityId = position.SecurityId,
            Side = TradeSide.Sell,
            DecidedOn = action.EffectiveDate,   // a forced event decides and fills on its effective date
            FilledOn = action.EffectiveDate,
            Shares = position.Shares,
            RawFillPrice = price,
            Commission = 0m,
            SpreadCost = 0m,
            ImpactCost = 0m,
            CostModelVersion = "corp-action-waived",   // stamps that this fill was NOT priced by the D43 model
            Reason = TradeReason.CorpAction,
            ActionId = action.ActionId,
            RunKind = runKind,
        };

    private static decimal RequirePositiveCash(CorporateAction action, string kind, string field)
    {
        if (action.CashPerShare is not { } cash || cash < 0m)
        {
            throw new InvalidOperationException(
                $"{kind} action {action.ActionId} has no non-negative {field} — cannot price it (fail closed).");
        }
        return cash;
    }

    private static (SecurityId AcquirerId, double Ratio) RequireExchange(CorporateAction action, string kind)
    {
        if (action.CounterpartySecurityId is not { } acquirer)
        {
            throw new InvalidOperationException(
                $"{kind} action {action.ActionId} has no counterparty security_id — there is no acquirer to convert into.");
        }
        if (action.Ratio is not { } ratio || ratio <= 0 || !double.IsFinite(ratio))
        {
            throw new InvalidOperationException(
                $"{kind} action {action.ActionId} has no positive, finite exchange ratio (got {action.Ratio?.ToString() ?? "null"}).");
        }
        return (acquirer, ratio);
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
