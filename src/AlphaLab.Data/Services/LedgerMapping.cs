using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// Translates between the pure Core ledger records and the EF rows.
///
/// WHY THE DUPLICATION IS WORTH IT. It would be cheaper to let the funnel and the §13.6 ledger
/// operate on EF rows directly. That would also put the phase's highest-risk logic — 10 corporate
/// -action fixtures point at it — behind a DbContext, so every ledger test would need SQLite, and
/// the D76 read-at-watermark rule would become a discipline ("remember to call GetActionsAsOf")
/// rather than a structural guarantee. Keeping Core free of Data means raw table access is
/// IMPOSSIBLE there, not merely discouraged. This file is the ~120 lines that buys both.
///
/// TOKEN MAPPING IS FAIL-CLOSED (hard rule 10). Every enum↔token conversion throws on an
/// unmapped value rather than writing a token the DB would reject at SaveChanges (or, worse, one
/// it would silently accept). This mirrors DataQualityFlagStore's IssueToken/SeverityToken.
/// </summary>
public static class LedgerMapping
{
    // ---- run_kind (the D37 quarantine discriminant) ----
    // Note the asymmetry with runs.run_kind, which also has 'catchup': a ledger row is written by
    // a forward run or a replay run, and 'live'/'catchup' collapse to Live here because nothing
    // downstream may treat a caught-up day as lesser evidence than a same-day one (hard rule 1).

    public static string RunKindToken(RunKind kind) => kind switch
    {
        RunKind.Live => "live",
        RunKind.Replay => "replay",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped RunKind."),
    };

    public static RunKind ParseRunKind(string token) => token switch
    {
        "live" or "catchup" => RunKind.Live,   // both are FORWARD evidence
        "replay" => RunKind.Replay,
        _ => throw new InvalidOperationException(
            $"Unmapped run_kind '{token}'. A ledger row of unknown provenance cannot be classified " +
            "as forward or replay, and guessing would breach the D37 quarantine."),
    };

    // ---- trades.side (the one CHECK on the ledger tables) ----

    public static string SideToken(TradeSide side) => side switch
    {
        TradeSide.Buy => "buy",
        TradeSide.Sell => "sell",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unmapped TradeSide."),
    };

    public static TradeSide ParseSide(string token) => token switch
    {
        "buy" => TradeSide.Buy,
        "sell" => TradeSide.Sell,
        _ => throw new InvalidOperationException($"Unmapped trades.side '{token}'."),
    };

    // ---- trades.reason (no CHECK; the constraint lives here and in the funnel) ----

    public static string ReasonToken(TradeReason reason) => reason switch
    {
        TradeReason.Wishlist => "wishlist",
        TradeReason.ExitPolicy => "exit_policy",
        TradeReason.CorpAction => "corp_action",
        TradeReason.Guardrail => "guardrail",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped TradeReason."),
    };

    public static TradeReason ParseReason(string token) => token switch
    {
        "wishlist" => TradeReason.Wishlist,
        "exit_policy" => TradeReason.ExitPolicy,
        "corp_action" => TradeReason.CorpAction,
        "guardrail" => TradeReason.Guardrail,
        _ => throw new InvalidOperationException($"Unmapped trades.reason '{token}'."),
    };

    // ---- cash_events.type (SCHEMA's list is deliberately open-ended: "dividend|merger_cash|deposit|…") ----

    public static string CashTypeToken(CashEventType type) => type switch
    {
        CashEventType.Dividend => "dividend",
        CashEventType.MergerCash => "merger_cash",
        CashEventType.Deposit => "deposit",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped CashEventType."),
    };

    public static CashEventType ParseCashType(string token) => token switch
    {
        "dividend" => CashEventType.Dividend,
        "merger_cash" => CashEventType.MergerCash,
        "deposit" => CashEventType.Deposit,
        _ => throw new InvalidOperationException($"Unmapped cash_events.type '{token}'."),
    };

    // ---- corporate_actions.type (the 8-value CHECK) ----

    /// <summary>Map the DB token to the Core enum, failing CLOSED on an unknown token (rule 10) rather
    /// than defaulting to a benign kind — an action type this build does not recognize is a stop, not a
    /// silent skip.</summary>
    public static CorporateActionType ParseCorporateActionType(string type) => type switch
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

    // ---- rows ----

    /// <summary>
    /// Row → domain for a corporate action. Lives here, beside every other row translation, rather than
    /// privately in the applier: D142 gave it a SECOND caller (the pending-order restatement, which asks
    /// about securities the account may not hold and so cannot go through the applier). Two copies of a
    /// mapper whose <see cref="CorporateAction.AppliedOn"/> decides WHICH DAY an action lands on is
    /// exactly the drift that would put the book and its orders back out of step.
    /// </summary>
    public static CorporateAction ToDomain(CorporateActionRow r)
    {
        ArgumentNullException.ThrowIfNull(r);

        return new CorporateAction
        {
            ActionId = r.ActionId,
            SecurityId = new SecurityId(r.SecurityId),
            Type = ParseCorporateActionType(r.Type),
            ExDate = r.ExDate,
            EffectiveDate = r.EffectiveDate,
            CashPerShare = r.CashPerShare,
            Ratio = r.Ratio,
            CounterpartySecurityId = r.CounterpartySecurityId is { } c ? new SecurityId(c) : null,
            NewSymbol = r.NewSymbol,
        };
    }

    public static AccountRow ToRow(Account a) => new()
    {
        AccountId = a.AccountId,
        StrategyId = a.StrategyId,
        StartingCash = a.StartingCash,
        RunKind = RunKindToken(a.RunKind),
    };

    public static Account ToDomain(AccountRow r) => new()
    {
        AccountId = r.AccountId,
        StrategyId = r.StrategyId,
        StartingCash = r.StartingCash,
        RunKind = ParseRunKind(r.RunKind),
    };

    public static PositionRow ToRow(Position p) => new()
    {
        AccountId = p.AccountId,
        SecurityId = p.SecurityId.Value,
        Shares = p.Shares,
        CostBasis = p.CostBasis,
        OpenedOn = p.OpenedOn,
        Frozen = p.Frozen,
        FrozenReason = p.FrozenReason,
    };

    public static Position ToDomain(PositionRow r) => new()
    {
        AccountId = r.AccountId,
        SecurityId = new SecurityId(r.SecurityId),
        Shares = r.Shares,
        CostBasis = r.CostBasis,
        OpenedOn = r.OpenedOn,
        Frozen = r.Frozen,
        FrozenReason = r.FrozenReason,
    };

    public static TradeRow ToRow(Trade t) => new()
    {
        TradeId = t.TradeId,
        AccountId = t.AccountId,
        SecurityId = t.SecurityId.Value,
        Side = SideToken(t.Side),
        DecidedOn = t.DecidedOn,
        FilledOn = t.FilledOn,
        Shares = t.Shares,
        RawFillPrice = t.RawFillPrice,
        Commission = t.Commission,
        SpreadCost = t.SpreadCost,
        ImpactCost = t.ImpactCost,
        CostModelVersion = t.CostModelVersion,
        Reason = ReasonToken(t.Reason),
        ActionId = t.ActionId,
        RunKind = RunKindToken(t.RunKind),
    };

    public static Trade ToDomain(TradeRow r) => new()
    {
        TradeId = r.TradeId,
        AccountId = r.AccountId,
        SecurityId = new SecurityId(r.SecurityId),
        Side = ParseSide(r.Side),
        DecidedOn = r.DecidedOn,
        FilledOn = r.FilledOn,
        Shares = r.Shares,
        RawFillPrice = r.RawFillPrice,
        Commission = r.Commission,
        SpreadCost = r.SpreadCost,
        ImpactCost = r.ImpactCost,
        CostModelVersion = r.CostModelVersion,
        Reason = ParseReason(r.Reason),
        ActionId = r.ActionId,
        RunKind = ParseRunKind(r.RunKind),
    };

    public static CashEventRow ToRow(CashEvent c) => new()
    {
        EventId = c.EventId,
        AccountId = c.AccountId,
        SecurityId = c.SecurityId?.Value,
        AsOf = c.AsOf,
        Type = CashTypeToken(c.Type),
        Amount = c.Amount,
        ActionId = c.ActionId,
        RunKind = RunKindToken(c.RunKind),
    };

    public static CashEvent ToDomain(CashEventRow r) => new()
    {
        EventId = r.EventId,
        AccountId = r.AccountId,
        SecurityId = r.SecurityId is { } id ? new SecurityId(id) : null,
        AsOf = r.AsOf,
        Type = ParseCashType(r.Type),
        Amount = r.Amount,
        ActionId = r.ActionId,
        RunKind = ParseRunKind(r.RunKind),
    };
}
