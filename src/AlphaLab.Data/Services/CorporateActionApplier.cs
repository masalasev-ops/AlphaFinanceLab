using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// One action the applier ACTED ON, for the day's audit / the pipeline's log.
///
/// <see cref="Effect"/> is the structured result — the same closed hierarchy the pure engine returned —
/// and it is load-bearing rather than decorative: the pipeline reads <c>PositionRestated.Ratio</c> off it
/// to restate that security's pending orders (D142). It used to be interpolated into a <c>Detail</c>
/// STRING and thrown away, which left the only machine-readable record of a split's ratio as a substring
/// of an English sentence.
///
/// <see cref="Detail"/> is now DERIVED from <see cref="Effect"/> rather than supplied beside it. As a
/// positional parameter it was whatever the caller passed, so "the prose and the data cannot drift"
/// would have been a claim nothing examined — the shape D140 forbids, in the very change made to satisfy
/// it. As a computed property the compiler is the enforcement, and no grep is needed (nor could one ever
/// go red).
/// </summary>
public sealed record AppliedCorporateAction(
    long ActionId, SecurityId Id, CorporateActionType Type, CorporateActionEffect Effect)
{
    /// <summary>The audit line, rendered from the effect. Every fact in it comes off
    /// <see cref="Effect"/>, so it cannot describe something the ledger did not do.</summary>
    public string Detail => FormatDetail(Effect);

    private static string FormatDetail(CorporateActionEffect effect) => effect switch
    {
        CorporateActionEffect.DividendCredited d =>
            $"dividend: {d.Shares} sh × {d.PerShare} = {d.Cash.Amount} credited on ex-date {d.Cash.AsOf}.",

        CorporateActionEffect.PositionRestated r =>
            $"split ×{r.Ratio}: {r.Before.Shares} → {r.After.Shares} sh, basis {r.After.CostBasis} unchanged.",

        CorporateActionEffect.TickerRenamedNoLedgerEffect t =>
            $"ticker change → '{t.NewSymbol}': no ledger effect (identity is security_id, D39).",

        CorporateActionEffect.PositionForceClosed f =>
            $"force-closed: sell {f.Sell.Shares} sh @ {f.Sell.RawFillPrice} (costs waived), position removed.",

        // The pre-D142 wording opened with the target's PRE-merger share count, which lives on the
        // position rather than on the effect. Rendering it here would mean passing it in beside the
        // effect — reintroducing exactly the supplied-not-derived shape this record exists to remove.
        // The count is dropped deliberately, and said so rather than quietly reworded: SharesConverted
        // and the acquirer are the substantive facts, and `trades` carries the history either way.
        CorporateActionEffect.StockMergerConverted s =>
            $"stock merger: converted into {s.SharesConverted} sh of {s.AcquirerAfter.SecurityId}, basis carried.",

        CorporateActionEffect.MixedMergerApplied m =>
            $"mixed merger: {m.Cash.Amount} cash + {m.SharesConverted} sh of {m.AcquirerAfter.SecurityId}.",

        CorporateActionEffect.SpinoffReceived sp =>
            $"spin-off: new {sp.SpinoffPosition.Shares} sh of {sp.SpinoffPosition.SecurityId}, " +
            $"basis {sp.SpinoffPosition.CostBasis} moved from parent (now {sp.ParentAfter.CostBasis}).",

        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unmapped corporate-action effect."),
    };
}

/// <summary>
/// An action that was DUE but had nothing to act on — the position was closed earlier the same day by a
/// terminal event, so the engine never ran and there is no effect.
///
/// Its own list rather than an <c>Effect</c>-less <see cref="AppliedCorporateAction"/>: a nullable field
/// that carries "nothing happened" makes every reader handle a case the type says is normal, and it let
/// <see cref="CorporateActionOutcome.Applied"/> report actions that were not applied. The audit stays
/// complete; the name stops overstating.
/// </summary>
public sealed record SkippedCorporateAction(long ActionId, SecurityId Id, CorporateActionType Type, string Reason);

/// <summary>A position frozen by the fail-closed stoppage check, with its reason.</summary>
public sealed record FrozenByStoppage(SecurityId Id, string Reason);

/// <summary>What one account's corporate-action pass did on one day.</summary>
public sealed record CorporateActionOutcome(
    IReadOnlyList<AppliedCorporateAction> Applied,
    IReadOnlyList<SkippedCorporateAction> Skipped,
    IReadOnlyList<FrozenByStoppage> Frozen);

/// <summary>
/// Applies the day's corporate actions to one account's ledger — the Data half of §13.6 part 1
/// (2.6). It resolves actions at the run's watermark (D76 — never raw table access), matches each to
/// a held position, runs the pure <see cref="CorporateActionLedger"/>, and persists the effect via
/// <see cref="ILedgerStore"/>. The engine decides WHAT happens; this decides WHICH actions apply
/// today and writes the result down.
///
/// D53 ORDERING. The daily pipeline (2.10) applies corporate actions BEFORE the funnel runs (bars →
/// actions → membership → regime → funnel), so by the time Stage 4 plans, the book is already
/// post-action. This service is that "actions" step for the ledger.
///
/// TRANSACTIONS ARE THE CALLER'S — like every Data service here, it calls SaveChanges (through the
/// store) but opens no transaction; the pipeline's one-transaction-per-day wraps it, and that
/// one-transaction-per-day plus `ux_runs_ok_forward` is what makes application idempotent (a day is
/// applied exactly once). There is no per-action "processed" flag (finding J / 2.7; the always-NULL
/// `processed_on` column was dropped by D94/M5).
/// </summary>
public sealed class CorporateActionApplier(
    ILedgerStore ledger,
    ICorporateActionReadService actions,
    IBarReadService bars,
    CorporateActionsOptions options)
{
    /// <summary>
    /// Apply every corporate action that became effective since the prior session — the window
    /// (<paramref name="previousSession"/>, <paramref name="asOf"/>] — to <paramref name="accountId"/>'s
    /// held positions, then run the fail-closed stoppage check over the surviving book.
    ///
    /// Actions are matched to HELD positions only: a dividend or split on a name the account does not
    /// hold has no ledger effect. Each held security's actions are resolved at
    /// <paramref name="watermark"/> and filtered to those whose <see cref="CorporateAction.AppliedOn"/>
    /// falls in the window. The window — not equality with asOf (finding 192) — is what lets an action
    /// whose effective date is a NON-SESSION day (a weekend split, a holiday merger close) apply on the
    /// next session instead of never; consecutive sessions partition the date line, so each action still
    /// applies exactly once. <paramref name="previousSession"/> null (no prior session in the calendar)
    /// widens the window to everything ≤ asOf — vacuous in practice, since no book exists before the
    /// first session.
    /// </summary>
    public CorporateActionOutcome ApplyForAccount(
        long accountId, RunKind runKind, string asOf, string watermark, string? previousSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);

        var applied = new List<AppliedCorporateAction>();
        var skipped = new List<SkippedCorporateAction>();
        var frozen = new List<FrozenByStoppage>();

        // Snapshot the held set up front. A split re-writes a position row (via UpsertPosition), so we
        // re-read the current position immediately before applying each action rather than trusting the
        // snapshot's stale shares — but the SET of securities to consider is fixed at the start of the day.
        var heldSecurities = ledger.GetPositions(accountId).Select(p => p.SecurityId).ToList();

        foreach (var securityId in heldSecurities)
        {
            // Resolve this security's actions at the watermark (D76), keep the (previousSession, asOf]
            // window (finding 192). Ordered by (effective_date, type) by the read service, so on a shared
            // date "dividend" precedes "split" lexically — the dividend is paid on the pre-split shares
            // of record, which is the correct order.
            var todays = actions.GetActionsAsOf(securityId.Value, watermark)
                .Select(LedgerMapping.ToDomain)
                .Where(a => CorporateActionWindow.Contains(a.AppliedOn, previousSession, asOf))
                .ToList();

            var hasTerminalToday = todays.Any(IsTerminal);

            foreach (var action in todays)
            {
                // Re-read: a prior same-day action (e.g. a split) may already have re-written the row.
                var position = ledger.GetPosition(accountId, securityId);
                if (position is null)
                {
                    // The account stopped holding it mid-day (a delist/merger close). Nothing left to
                    // apply — but record the skip so the day's audit is complete rather than silent. It
                    // goes in `skipped`, not `applied`: nothing was applied, and a list that says
                    // otherwise is a claim the row itself contradicts.
                    skipped.Add(new SkippedCorporateAction(action.ActionId, securityId, action.Type,
                        "the position was closed earlier in the day."));
                    continue;
                }

                var context = BuildContext(accountId, position, action, asOf, watermark);
                applied.Add(Persist(CorporateActionLedger.Apply(position, action, runKind, context), position, action));
            }

            // Fail-closed stoppage check (§13.6/rule 10) AFTER any actions: a held name with no bar today
            // and no terminal event to explain it freezes at the last print.
            var stillHeld = ledger.GetPosition(accountId, securityId);
            if (stillHeld is not null)
            {
                var hasBar = bars.GetBar(securityId.Value, asOf, watermark) is not null;
                var reason = CorporateActionLedger.StoppageFreezeReason(stillHeld, hasBar, hasTerminalToday, asOf);
                if (reason is not null)
                {
                    ledger.FreezePosition(accountId, securityId, reason);
                    frozen.Add(new FrozenByStoppage(securityId, reason));
                }
            }
        }

        return new CorporateActionOutcome(applied, skipped, frozen);
    }

    /// <summary>Assemble the extra facts a part-2 kind needs. Part-1 kinds ignore all of it, so this is
    /// cheap to build unconditionally; each part-2 handler validates only what it uses.</summary>
    private CorporateActionContext BuildContext(
        long accountId, Position position, CorporateAction action, string asOf, string watermark)
    {
        // The acquirer position (if the account already holds it) — a stock/mixed merger sums into it.
        Position? counterparty = action.CounterpartySecurityId is { } cp
            ? ledger.GetPosition(accountId, cp)
            : null;

        // The last available RAW print for a delist force-exit: the asOf bar's close at the watermark.
        decimal? lastPrint = action.Type == CorporateActionType.Delist
            ? (decimal?)bars.GetBar(action.SecurityId.Value, asOf, watermark)?.Close
            : null;

        // Spin-off terms, resolved by ratio (primary) or first-print relative value (fallback).
        double? spinoffShares = null;
        decimal? spinoffBasis = null;
        if (action.Type == CorporateActionType.Spinoff)
        {
            var terms = ResolveSpinoff(position, action, asOf, watermark);
            spinoffShares = terms.SpinoffShares;
            spinoffBasis = terms.BasisToSpinoff;
        }

        return new CorporateActionContext
        {
            ExistingCounterpartyPosition = counterparty,
            LastPrintPrice = lastPrint,
            BankruptcyHaircut = action.Type == CorporateActionType.Delist ? options.BankruptcyHaircutPct / 100.0 : null,
            SpinoffShares = spinoffShares,
            SpinoffBasisAllocated = spinoffBasis,
        };
    }

    private SpinoffTerms ResolveSpinoff(Position parent, CorporateAction action, string asOf, string watermark)
    {
        // Primary: ratio in the feed → shares × ratio, share-proportional basis (no prices needed).
        if (action.Ratio is { } ratio && ratio > 0 && double.IsFinite(ratio))
        {
            return SpinoffAllocation.ByRatio(parent.Shares, parent.CostBasis, ratio);
        }

        // Fallback: ratio missing → first-print relative value. Needs the parent's post-spin first price
        // and the spun-off entity's first price. Fail closed if either is absent (rule 10).
        if (action.CounterpartySecurityId is not { } spinoffId)
        {
            throw new InvalidOperationException(
                $"Spin-off action {action.ActionId} has neither a ratio nor a counterparty security_id — nothing to receive.");
        }
        var parentPrice = bars.GetBar(parent.SecurityId.Value, asOf, watermark)?.Close;
        var spinoffPrice = bars.GetBar(spinoffId.Value, asOf, watermark)?.Close;
        if (parentPrice is not { } pp || spinoffPrice is not { } sp)
        {
            throw new InvalidOperationException(
                $"Spin-off action {action.ActionId} has no ratio and is missing a first print (parent={parentPrice}, " +
                $"spin-off={spinoffPrice}) — the first-print allocation fallback cannot run. Fail closed (§13.6/rule 10).");
        }
        return SpinoffAllocation.ByFirstPrint(parent.Shares, parent.CostBasis, pp, sp);
    }

    /// <summary>Write the effect down. It no longer composes an audit string — the effect IS the record,
    /// and <see cref="AppliedCorporateAction.Detail"/> renders it. This method's whole job is the ledger
    /// writes, which is why every arm now differs only in those.</summary>
    private AppliedCorporateAction Persist(CorporateActionEffect effect, Position current, CorporateAction action)
    {
        switch (effect)
        {
            case CorporateActionEffect.DividendCredited d:
                ledger.RecordCashEvent(d.Cash);
                break;

            case CorporateActionEffect.PositionRestated r:
                ledger.UpsertPosition(r.After);
                break;

            case CorporateActionEffect.TickerRenamedNoLedgerEffect:
                // D39: nothing to persist. The alias was updated in ticker_history at ingestion; the
                // position keeps its security_id. Recorded so the audit shows the non-event explicitly.
                break;

            case CorporateActionEffect.PositionForceClosed f:
                // Cash merger / delist: a forced sell with costs waived, then the position is removed.
                ledger.RecordTrade(f.Sell);
                Remove(current);
                break;

            case CorporateActionEffect.StockMergerConverted s:
                Remove(current);                        // target gone
                ledger.UpsertPosition(s.AcquirerAfter); // converted into the acquirer, basis carried
                break;

            case CorporateActionEffect.MixedMergerApplied m:
                ledger.RecordCashEvent(m.Cash);         // cash leg
                Remove(current);                        // target gone
                ledger.UpsertPosition(m.AcquirerAfter); // stock leg
                break;

            case CorporateActionEffect.SpinoffReceived sp:
                ledger.UpsertPosition(sp.ParentAfter);      // parent basis reduced, shares unchanged
                ledger.UpsertPosition(sp.SpinoffPosition);  // new receipt, enters even if not in-index
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unmapped corporate-action effect.");
        }

        return new AppliedCorporateAction(action.ActionId, action.SecurityId, action.Type, effect);
    }

    /// <summary>Remove a position by upserting it to zero shares (which deletes the row — positions is
    /// current state, not a log; the trades log keeps the history).</summary>
    private void Remove(Position position) => ledger.UpsertPosition(position with { Shares = 0 });

    /// <summary>Does a corporate action explain why a HELD name has no bar today? Only the events that
    /// STOP the name trading do — a cash/stock/mixed merger (the target is absorbed) or a delist. A
    /// SPIN-OFF does NOT: the parent keeps trading, so a spin-off with a missing parent bar is still an
    /// unexplained stoppage and must still freeze.</summary>
    private static bool IsTerminal(CorporateAction a) => a.Type is
        CorporateActionType.MergerCash or CorporateActionType.MergerStock or CorporateActionType.MergerMixed
        or CorporateActionType.Delist;

    // Row → domain moved to LedgerMapping.ToDomain(CorporateActionRow) when D142 gave it a second
    // caller (ShareUnitRestatements, which asks about securities this account may not hold).
}
