using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Funnel;

/// <summary>
/// Everything the executor needs to judge one day's exits. Assembled by the caller so the executor
/// stays pure — and so the two facts that are easy to fake (today's ranks, whether today is a
/// rebalance) arrive as data rather than being derived inside a switch.
/// </summary>
public sealed record ExitContext
{
    public required DateOnly AsOf { get; init; }

    /// <summary>Today's cross-sectional rank per scored security: **1 = highest score**. A security
    /// ABSENT from this map was not scored today (the model omitted it — catalog §2 says a name with
    /// insufficient history is omitted, which is an ordinary answer, not an error).</summary>
    public required IReadOnlyDictionary<SecurityId, int> Ranks { get; init; }

    /// <summary>Today's Stage-3 wish list. Read ONLY by <see cref="ExitPolicy.ScheduledRebalance"/>,
    /// and only on a rebalance day — see the note on rule 7 in <see cref="ExitPolicyExecutor"/>.</summary>
    public required IReadOnlySet<SecurityId> WishList { get; init; }

    /// <summary>Sessions elapsed since the account's inception, supplied by the caller from the
    /// trading calendar (Core has no calendar). Drives the rebalance schedule. Counted in SESSIONS,
    /// never in calendar days — "every 21 days" means 21 trading days, and a month of weekends
    /// would otherwise silently shift every rebalance.</summary>
    public required int SessionsSinceInception { get; init; }
}

/// <summary>What the policy decided for one position. A closed hierarchy so a caller cannot forget
/// a case, and both outcomes carry a reason — a close nobody can explain is not an audit trail.</summary>
public abstract record ExitVerdict
{
    private ExitVerdict() { }

    public sealed record Hold(string Reason) : ExitVerdict;

    public sealed record Close(string Reason) : ExitVerdict;
}

/// <summary>
/// Executes the declarative <see cref="ExitPolicy"/> shapes (catalog §2). SHARED code — the exit
/// MECHANICS are identical for every strategy; only the declared policy differs. PURE: no DB, no
/// clock, no prices.
///
/// THIS TYPE IS THE ONLY THING IN THE SYSTEM THAT MAY CLOSE A POSITION ON SIGNAL (hard rule 7).
/// The wish list opens and adds; falling off it is NEVER an implicit sell. The only other closes
/// are forced events — corporate actions (§13.6) and guardrail circuit-breakers — and those do not
/// come through here (see below).
///
/// Why rule 7 is a rule and not a preference: "sell what's no longer in the top N" sounds like the
/// obvious complement to "buy the top N", but it silently converts every strategy into a daily
/// rebalancer. A name oscillating around rank N would be bought and sold repeatedly, paying the
/// full D43 cost each way, and the resulting track record would measure boundary noise rather than
/// the signal. That is exactly what RankBuffer's hysteresis exists to prevent — and it prevents
/// nothing if the wish list can close a position behind its back.
///
/// FORCED EVENTS DO NOT ROUTE THROUGH HERE, and that is structural rather than an omission. D53's
/// Stage-2 order applies corporate actions BEFORE the funnel runs (bars → actions → membership →
/// regime label → funnel + fills), so by the time Stage 4 plans, the book has already absorbed
/// every merger, spin-off, and delist. §13.6 builds those trades directly with costs waived; the
/// planner simply sees the post-action book. (2.6/2.7 build that path.)
///
/// PHASING: three of the five shapes execute. TargetOrTimeStop and ChannelExit REFUSE, naming their
/// Phase-6 owners — TargetOrTimeStop's `exitCondition` is an opaque token the catalog never defines,
/// and inventing a reversion condition would silently invent strategy behaviour (rule 10).
/// </summary>
public static class ExitPolicyExecutor
{
    /// <summary>Judge one held position against its strategy's policy.</summary>
    public static ExitVerdict Evaluate(ExitPolicy policy, SecurityId held, ExitContext context)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        return policy switch
        {
            // Buy-and-hold: nothing the signal does can close this. Only forced events can, and they
            // never reach this switch.
            ExitPolicy.Never => new ExitVerdict.Hold("policy is Never — only a forced event can close."),

            ExitPolicy.RankBuffer rb => EvaluateRankBuffer(rb, held, context),

            ExitPolicy.ScheduledRebalance sr => EvaluateScheduledRebalance(sr, held, context),

            ExitPolicy.TargetOrTimeStop t => throw new NotSupportedException(
                $"ExitPolicy.TargetOrTimeStop (exitCondition='{t.ExitCondition}', maxHoldDays={t.MaxHoldDays}) is " +
                "declared but not executable in this build. The catalog (§2/§6.2) names the shape but never " +
                "defines the reversion condition's grammar, so there is nothing to evaluate — guessing one would " +
                "invent strategy behaviour. Phase 6's MeanReversionModel owns both the grammar and its evaluation."),

            ExitPolicy.ChannelExit c => throw new NotSupportedException(
                $"ExitPolicy.ChannelExit(exitChannel={c.ExitChannel}) is declared but not executable in this " +
                "build. Phase 6's BreakoutModel owns it."),

            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unmapped ExitPolicy shape."),
        };
    }

    /// <summary>
    /// Momentum's hysteresis (catalog §6.1): enter at rank ≤ N, exit only once rank falls past
    /// <c>ExitRank</c> (~2N). The gap between the two is the whole point — it is what stops a name
    /// hovering at the selection boundary from churning.
    ///
    /// AN UNSCORED NAME IS HELD, NOT CLOSED. If the model omitted the security today it has no rank,
    /// and "no rank" is not "bad rank": the model said nothing, it did not say sell. Closing on a
    /// missing input would be failing OPEN into an irreversible action, which inverts rule 10 —
    /// a missing input rejects an ORDER, it does not manufacture one. A name that stops being
    /// scorable forever is a delisting, and §13.6's force-exit owns that, not this policy.
    /// </summary>
    private static ExitVerdict EvaluateRankBuffer(ExitPolicy.RankBuffer policy, SecurityId held, ExitContext context)
    {
        if (!context.Ranks.TryGetValue(held, out var rank))
        {
            return new ExitVerdict.Hold(
                $"not scored on {context.AsOf:yyyy-MM-dd} — no rank to judge against the buffer of " +
                $"{policy.ExitRank}. The model omitted the name; it did not say sell.");
        }

        return rank > policy.ExitRank
            ? new ExitVerdict.Close($"rank {rank} fell past the exit buffer of {policy.ExitRank}.")
            : new ExitVerdict.Hold($"rank {rank} is still within the exit buffer of {policy.ExitRank}.");
    }

    /// <summary>
    /// Hold to the next rebalance (catalog §2; the D68 equal-weight benchmark's monthly cadence).
    /// Between rebalances NOTHING closes, whatever the wish list says. ON a rebalance day the book
    /// becomes the current selection, so a held name that is no longer selected is closed.
    ///
    /// Is that "falling off the wish list closes a position", i.e. a rule-7 violation? No — and the
    /// distinction is the point of the rule. Rule 7 forbids an IMPLICIT sell: the wish list quietly
    /// closing positions behind the policy's back, every day. Here the POLICY is the authority and
    /// the wish list is merely its input: ScheduledRebalance's declared meaning is "at each
    /// rebalance, hold exactly the current selection". The close is attributed to
    /// <see cref="TradeReason.ExitPolicy"/> because the policy is genuinely what closed it, and on
    /// the other 20 days of the month the same name falling off the wish list does nothing at all.
    /// </summary>
    private static ExitVerdict EvaluateScheduledRebalance(
        ExitPolicy.ScheduledRebalance policy, SecurityId held, ExitContext context)
    {
        if (policy.EveryNDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy), policy.EveryNDays,
                "ScheduledRebalance.EveryNDays must be positive; a non-positive cadence has no meaning and " +
                "would make every day (or no day) a rebalance.");
        }

        if (!IsRebalanceDay(policy.EveryNDays, context.SessionsSinceInception))
        {
            var next = policy.EveryNDays - context.SessionsSinceInception % policy.EveryNDays;
            return new ExitVerdict.Hold(
                $"not a rebalance day (session {context.SessionsSinceInception} since inception; every " +
                $"{policy.EveryNDays}; next in {next}). Falling off the wish list closes nothing today.");
        }

        return context.WishList.Contains(held)
            ? new ExitVerdict.Hold($"rebalance day and still selected — held through session {context.SessionsSinceInception}.")
            : new ExitVerdict.Close(
                $"rebalance day (session {context.SessionsSinceInception} since inception; every " +
                $"{policy.EveryNDays}) and no longer selected.");
    }

    /// <summary>Session 0 (inception) counts as a rebalance day — it is when the book is first
    /// built, so treating it as anything else would delay the first entry by a full cadence.</summary>
    public static bool IsRebalanceDay(int everyNDays, int sessionsSinceInception) =>
        everyNDays > 0 && sessionsSinceInception % everyNDays == 0;
}
