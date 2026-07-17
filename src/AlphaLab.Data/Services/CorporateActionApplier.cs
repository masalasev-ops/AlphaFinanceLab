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
/// applied exactly once). There is no per-action "processed" flag — `corporate_actions.processed_on`
/// is never written (finding J / 2.7).
/// </summary>
public sealed class CorporateActionApplier(
    ILedgerStore ledger,
    ICorporateActionReadService actions,
    IBarReadService bars)
{
    /// <summary>
    /// Apply every corporate action effective on <paramref name="asOf"/> to <paramref name="accountId"/>'s
    /// held positions, then run the fail-closed stoppage check over the surviving book.
    ///
    /// Actions are matched to HELD positions only: a dividend or split on a name the account does not
    /// hold has no ledger effect. Each held security's actions are resolved at
    /// <paramref name="watermark"/> and filtered to those whose <see cref="CorporateAction.AppliedOn"/>
    /// equals <paramref name="asOf"/>.
    /// </summary>
    public CorporateActionOutcome ApplyForAccount(long accountId, RunKind runKind, string asOf, string watermark)
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
            // Resolve this security's actions at the watermark (D76), keep only today's. Ordered by
            // (effective_date, type) by the read service, so on a shared date "dividend" precedes "split"
            // lexically — the dividend is paid on the pre-split shares of record, which is the correct order.
            var todays = actions.GetActionsAsOf(securityId.Value, watermark)
                .Select(ToDomain)
                .Where(a => a.AppliedOn == asOf)
                .ToList();

            var hasTerminalToday = todays.Any(IsTerminal);

            foreach (var action in todays)
            {
                // Re-read: a prior same-day action (e.g. a split) may already have re-written the row.
                var position = ledger.GetPosition(accountId, securityId);
                if (position is null)
                {
                    // The account stopped holding it mid-day (a 2.7 delist/merger close). Nothing left to
                    // apply — but record the skip so the day's audit is complete rather than silent.
                    applied.Add(new AppliedCorporateAction(action.ActionId, securityId, action.Type,
                        "skipped: the position was closed earlier in the day."));
                    continue;
                }

                applied.Add(Persist(CorporateActionLedger.Apply(position, action, runKind), action));
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

    private AppliedCorporateAction Persist(CorporateActionEffect effect, CorporateAction action)
    {
        switch (effect)
        {
            case CorporateActionEffect.DividendCredited d:
                ledger.RecordCashEvent(d.Cash);
                return new AppliedCorporateAction(action.ActionId, action.SecurityId, action.Type,
                    $"dividend: {d.Shares} sh × {d.PerShare} = {d.Cash.Amount} credited on ex-date {d.Cash.AsOf}.");

            case CorporateActionEffect.PositionRestated r:
                ledger.UpsertPosition(r.After);
                return new AppliedCorporateAction(action.ActionId, action.SecurityId, action.Type,
                    $"split ×{r.Ratio}: {r.Before.Shares} → {r.After.Shares} sh, basis {r.After.CostBasis} unchanged.");

            case CorporateActionEffect.TickerRenamedNoLedgerEffect t:
                // D39: nothing to persist. The alias was updated in ticker_history at ingestion; the
                // position keeps its security_id. Recorded so the audit shows the non-event explicitly.
                return new AppliedCorporateAction(action.ActionId, action.SecurityId, action.Type,
                    $"ticker change → '{t.NewSymbol}': no ledger effect (identity is security_id, D39).");

            default:
                throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unmapped corporate-action effect.");
        }
    }

    private static bool IsTerminal(CorporateAction a) => a.Type is
        CorporateActionType.MergerCash or CorporateActionType.MergerStock or CorporateActionType.MergerMixed
        or CorporateActionType.Spinoff or CorporateActionType.Delist;

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
