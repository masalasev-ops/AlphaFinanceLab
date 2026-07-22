using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>One action the applier acted on, for the day's audit / the pipeline's log.</summary>
public sealed record AppliedCorporateAction(long ActionId, SecurityId Id, CorporateActionType Type, string Detail);

/// <summary>A position frozen by the fail-closed stoppage check, with its reason.</summary>
public sealed record FrozenByStoppage(SecurityId Id, string Reason);

/// <summary>What one account's corporate-action pass did on one day.</summary>
public sealed record CorporateActionOutcome(
    IReadOnlyList<AppliedCorporateAction> Applied,
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
                .Select(ToDomain)
                .Where(a => string.CompareOrdinal(a.AppliedOn, asOf) <= 0
                            && (previousSession is null || string.CompareOrdinal(a.AppliedOn, previousSession) > 0))
                .ToList();

            var hasTerminalToday = todays.Any(IsTerminal);

            foreach (var action in todays)
            {
                // Re-read: a prior same-day action (e.g. a split) may already have re-written the row.
                var position = ledger.GetPosition(accountId, securityId);
                if (position is null)
                {
                    // The account stopped holding it mid-day (a delist/merger close). Nothing left to
                    // apply — but record the skip so the day's audit is complete rather than silent.
                    applied.Add(new AppliedCorporateAction(action.ActionId, securityId, action.Type,
                        "skipped: the position was closed earlier in the day."));
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

        return new CorporateActionOutcome(applied, frozen);
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

    private AppliedCorporateAction Persist(CorporateActionEffect effect, Position current, CorporateAction action)
    {
        switch (effect)
        {
            case CorporateActionEffect.DividendCredited d:
                ledger.RecordCashEvent(d.Cash);
                return Note(action, $"dividend: {d.Shares} sh × {d.PerShare} = {d.Cash.Amount} credited on ex-date {d.Cash.AsOf}.");

            case CorporateActionEffect.PositionRestated r:
                ledger.UpsertPosition(r.After);
                return Note(action, $"split ×{r.Ratio}: {r.Before.Shares} → {r.After.Shares} sh, basis {r.After.CostBasis} unchanged.");

            case CorporateActionEffect.TickerRenamedNoLedgerEffect t:
                // D39: nothing to persist. The alias was updated in ticker_history at ingestion; the
                // position keeps its security_id. Recorded so the audit shows the non-event explicitly.
                return Note(action, $"ticker change → '{t.NewSymbol}': no ledger effect (identity is security_id, D39).");

            case CorporateActionEffect.PositionForceClosed f:
                // Cash merger / delist: a forced sell with costs waived, then the position is removed.
                ledger.RecordTrade(f.Sell);
                Remove(current);
                return Note(action, $"force-closed: sell {f.Sell.Shares} sh @ {f.Sell.RawFillPrice} (costs waived), position removed.");

            case CorporateActionEffect.StockMergerConverted s:
                Remove(current);                      // target gone
                ledger.UpsertPosition(s.AcquirerAfter); // converted into the acquirer, basis carried
                return Note(action, $"stock merger: {current.Shares} sh → {s.SharesConverted} sh of {s.AcquirerAfter.SecurityId}, basis carried.");

            case CorporateActionEffect.MixedMergerApplied m:
                ledger.RecordCashEvent(m.Cash);       // cash leg
                Remove(current);                      // target gone
                ledger.UpsertPosition(m.AcquirerAfter); // stock leg
                return Note(action, $"mixed merger: {m.Cash.Amount} cash + {m.SharesConverted} sh of {m.AcquirerAfter.SecurityId}.");

            case CorporateActionEffect.SpinoffReceived sp:
                ledger.UpsertPosition(sp.ParentAfter);      // parent basis reduced, shares unchanged
                ledger.UpsertPosition(sp.SpinoffPosition);  // new receipt, enters even if not in-index
                return Note(action, $"spin-off: new {sp.SpinoffPosition.Shares} sh of {sp.SpinoffPosition.SecurityId}, " +
                    $"basis {sp.SpinoffPosition.CostBasis} moved from parent (now {sp.ParentAfter.CostBasis}).");

            default:
                throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unmapped corporate-action effect.");
        }
    }

    /// <summary>Remove a position by upserting it to zero shares (which deletes the row — positions is
    /// current state, not a log; the trades log keeps the history).</summary>
    private void Remove(Position position) => ledger.UpsertPosition(position with { Shares = 0 });

    private static AppliedCorporateAction Note(CorporateAction action, string detail) =>
        new(action.ActionId, action.SecurityId, action.Type, detail);

    /// <summary>Does a corporate action explain why a HELD name has no bar today? Only the events that
    /// STOP the name trading do — a cash/stock/mixed merger (the target is absorbed) or a delist. A
    /// SPIN-OFF does NOT: the parent keeps trading, so a spin-off with a missing parent bar is still an
    /// unexplained stoppage and must still freeze.</summary>
    private static bool IsTerminal(CorporateAction a) => a.Type is
        CorporateActionType.MergerCash or CorporateActionType.MergerStock or CorporateActionType.MergerMixed
        or CorporateActionType.Delist;

    private static CorporateAction ToDomain(CorporateActionRow row) => new()
    {
        ActionId = row.ActionId,
        SecurityId = new SecurityId(row.SecurityId),
        Type = ParseType(row.Type),
        ExDate = row.ExDate,
        EffectiveDate = row.EffectiveDate,
        CashPerShare = row.CashPerShare,
        Ratio = row.Ratio,
        CounterpartySecurityId = row.CounterpartySecurityId is { } c ? new SecurityId(c) : null,
        NewSymbol = row.NewSymbol,
    };

    /// <summary>Map the DB token to the Core enum, failing CLOSED on an unknown token (rule 10) rather
    /// than defaulting to a benign kind — an action type this build does not recognize is a stop, not a
    /// silent skip.</summary>
    private static CorporateActionType ParseType(string type) => type switch
    {
        "dividend" => CorporateActionType.Dividend,
        "split" => CorporateActionType.Split,
        "ticker_change" => CorporateActionType.TickerChange,
        "merger_cash" => CorporateActionType.MergerCash,
        "merger_stock" => CorporateActionType.MergerStock,
        "merger_mixed" => CorporateActionType.MergerMixed,
        "spinoff" => CorporateActionType.Spinoff,
        "delist" => CorporateActionType.Delist,
        _ => throw new InvalidOperationException(
            $"Unknown corporate_actions.type '{type}'. The ledger refuses an action kind it cannot map " +
            "rather than defaulting it (rule 10)."),
    };
}
