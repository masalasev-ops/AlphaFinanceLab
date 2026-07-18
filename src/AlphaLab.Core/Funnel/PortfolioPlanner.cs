using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Funnel;

/// <summary>A position the ExitPolicy decided to close, with the reason that closed it.</summary>
public sealed record PlannedClose(SecurityId Id, TradeReason Reason, string Detail);

/// <summary>
/// Which positions Stage 5 re-sizes today. See <see cref="PortfolioPlanner"/> for why this is not
/// simply "all of them, every day".
/// </summary>
public enum RebalanceScope
{
    /// <summary>Only today's new opens are sized. Existing positions are left exactly as they are —
    /// their weights drift with price, and that drift is not corrected.</summary>
    OpensOnly,

    /// <summary>A scheduled rebalance: the whole surviving book is re-sized to target.</summary>
    WholeBook,
}

/// <summary>The Stage-4 outcome: what the book should become, and why.</summary>
public sealed record PortfolioPlan
{
    /// <summary>On the wish list and not currently held — new positions.</summary>
    public required IReadOnlyList<SecurityId> Opens { get; init; }

    /// <summary>Held and surviving the ExitPolicy.</summary>
    public required IReadOnlyList<SecurityId> Holds { get; init; }

    /// <summary>Held and closed by the ExitPolicy.</summary>
    public required IReadOnlyList<PlannedClose> Closes { get; init; }

    public required RebalanceScope Scope { get; init; }

    /// <summary>Every hold/close/skip decision, with its reason — bound for stage_json.</summary>
    public required IReadOnlyList<FunnelNote> Notes { get; init; }

    /// <summary>The names Stage 5 sizes today: the new opens, plus the surviving book on a
    /// rebalance day. Never includes a close — a closed name is sold in full, not sized.</summary>
    public IReadOnlyList<SecurityId> ToSize => Scope == RebalanceScope.WholeBook
        ? Holds.Concat(Opens).Distinct().OrderBy(x => x.Value).ToList()
        : Opens;
}

/// <summary>
/// Stage 4 of the daily funnel (MASTER §6) — open / add / trim / exit. PURE.
///
/// THE RULE (hard rule 7): the wish list OPENS and ADDS; only the ExitPolicy CLOSES.
///
/// A UNIVERSE EXIT IS NEVER A CLOSE (D74). A name leaving the index stamps
/// `index_membership.removed_on` and nothing else. It drops out of Stage 1's eligible pool, so it
/// stops being scored and stops appearing on wish lists — but the POSITION is untouched, and this
/// planner will not close it. Only the ExitPolicy or a §13.6 forced event can. That is not a
/// technicality: index removal and delisting are different events that happen to correlate, and
/// treating the first as the second would fabricate a sale at an index-committee date rather than
/// at a market event. A real delisting arrives through the exchange symbol-list drop-out feed and
/// force-exits via §13.6; an index removal just means "we stopped picking it".
///
/// FORCED EVENTS ARE ALREADY APPLIED when this runs. D53's Stage-2 order puts corporate actions
/// before the funnel, so the positions handed in here are the post-action book. §13.6 (2.6/2.7)
/// owns those trades; this planner never sees them.
/// </summary>
public static class PortfolioPlanner
{
    /// <summary>
    /// Plan the book for <paramref name="context"/>.AsOf.
    ///
    /// <paramref name="held"/> is the CURRENT book (post-corporate-action). <paramref name="policy"/>
    /// is the strategy's declared exit rule. The wish list arrives on <paramref name="context"/>.
    /// </summary>
    public static PortfolioPlan Plan(
        IReadOnlyList<Position> held,
        ExitPolicy policy,
        ExitContext context)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        var holds = new List<SecurityId>();
        var closes = new List<PlannedClose>();
        var notes = new List<FunnelNote>();

        // F-DET: iterate the book in a stable order so stage_json is byte-identical across runs.
        foreach (var position in held.OrderBy(p => p.SecurityId.Value))
        {
            // A frozen position is unpriceable (rule 10 / §13.6: an unmapped action or a bar stoppage
            // pinned its valuation at the last print). Trading it would price a fill at a number we
            // have already declared untrustworthy, so it is held, untouched, until an operator
            // resolves the freeze. Not an exit decision at all — the policy is never consulted.
            if (position.Frozen)
            {
                holds.Add(position.SecurityId);
                notes.Add(new FunnelNote(position.SecurityId,
                    $"frozen ({position.FrozenReason}) — held untouched; the ExitPolicy is not consulted on a " +
                    "position whose price we have already declared untrustworthy."));
                continue;
            }

            var verdict = ExitPolicyExecutor.Evaluate(policy, position.SecurityId, context);
            switch (verdict)
            {
                case ExitVerdict.Close close:
                    closes.Add(new PlannedClose(position.SecurityId, TradeReason.ExitPolicy, close.Reason));
                    notes.Add(new FunnelNote(position.SecurityId, $"closed by ExitPolicy: {close.Reason}"));
                    break;
                case ExitVerdict.Hold hold:
                    holds.Add(position.SecurityId);
                    notes.Add(new FunnelNote(position.SecurityId, $"held: {hold.Reason}"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(policy), verdict, "Unmapped exit verdict.");
            }
        }

        // The wish list opens what is not already held. It never closes anything — a name absent from
        // today's wish list simply does not appear in this loop, and that is the whole of rule 7.
        var heldIds = held.Select(p => p.SecurityId).ToHashSet();
        var opens = context.WishList
            .Where(id => !heldIds.Contains(id))
            .OrderBy(id => id.Value)
            .ToList();

        return new PortfolioPlan
        {
            Opens = opens,
            Holds = holds,
            Closes = closes,
            Scope = ScopeFor(policy, context),
            Notes = notes,
        };
    }

    /// <summary>
    /// WHICH POSITIONS GET RE-SIZED TODAY — a seam the design leaves open, resolved here.
    ///
    /// The tempting default is "re-size the whole book to target every day". It is wrong, and
    /// §5.1's own acceptance criterion is what proves it: Buy&amp;Hold must "enter once, never churn,
    /// and pay ONE entry cost". Under daily rebalancing B&amp;H would trade every single day, nudging
    /// each position back to target as prices drift, and would fail that test by construction.
    /// Momentum says the same thing from the other side: the catalog insists it is "never run
    /// without the rank buffer" because the buffer kills boundary-churn cost bleed — and the buffer
    /// saves nothing if the sizer re-trades the book behind it every evening.
    ///
    /// So: drift is ALLOWED. A position is sized when it is opened and then left alone until the
    /// policy says otherwise. Only <see cref="ExitPolicy.ScheduledRebalance"/> re-sizes, and only on
    /// its own cadence — which is exactly what "trim" means in §6's Stage-4 description, and what
    /// D68's monthly equal-weight rebalance is.
    ///
    /// This composes correctly across all three Phase-2 policies: Never → B&amp;H sizes once and never
    /// churns; ScheduledRebalance(21) → the D68 benchmark re-weights monthly; RankBuffer → momentum
    /// sizes each entry once and exits on rank, never on drift.
    ///
    /// NOT DONE HERE: continuously enforcing Sizing.PositionCapPct against drift. A position that
    /// drifts past the cap is a real exposure, but trimming it daily would reintroduce exactly the
    /// churn above. The cap binds at sizing time; live exposure guardrails are FR-17 (Phase 7).
    /// </summary>
    private static RebalanceScope ScopeFor(ExitPolicy policy, ExitContext context) => policy switch
    {
        ExitPolicy.ScheduledRebalance sr
            when ExitPolicyExecutor.IsRebalanceDay(sr.EveryNDays, context.SessionsSinceInception)
            => RebalanceScope.WholeBook,
        _ => RebalanceScope.OpensOnly,
    };
}
