namespace AlphaLab.Data.Entities;

// The eight Phase-2 ledger tables (SCHEMA_v1.9 §"STRATEGIES, ACCOUNTS, LEDGER (D29, D30, D43)").
// Dates/timestamps are TEXT (trading date / UTC ISO-8601) per SCHEMA.
//
// MONEY IS decimal (D69) — mapped to TEXT explicitly in AlphaLabDbContext. Market-data prices and
// derived statistics stay REAL (double), and so do SHARE COUNTS: `shares` is a quantity, not
// money, and splits produce genuine fractions. Never bind a money column to double.
//
// Following the Phase-0/1 precedent, the SCHEMA `REFERENCES` links are documentation only — no EF
// navigations or FK constraints are declared, so no shadow indexes appear and the migration stays
// a 1:1 projection of the DDL.
//
// CHECK constraints: SCHEMA declares exactly ONE on these eight tables — trades.side. It declares
// NONE on strategies.status, accounts.run_kind, cash_events.type, or trades.reason, so none is
// added here (the Runs_Status_IsUnconstrained_OnlyRunKindHasACheck precedent: the on-disk DDL
// matches SCHEMA exactly, no more and no less). Those columns are constrained in code.

/// <summary>
/// strategies — the registry. strategy_id is a TEXT PK (e.g. 'momentum:L126:K21:N40'), so unlike
/// the rowid tables it carries no autoincrement question at all.
/// config_json is FROZEN (D17): a change forks a new strategy_id and increments trials_registry.
/// </summary>
public sealed class StrategyRow
{
    public string StrategyId { get; set; } = default!;
    /// <summary>momentum|meanrev|lowvol|passive|control|…</summary>
    public string Family { get; set; } = default!;
    /// <summary>Frozen params + seed (D17).</summary>
    public string ConfigJson { get; set; } = default!;
    public string ExitPolicyJson { get; set; } = default!;
    /// <summary>Nullable: two of the three HoldingHorizon shapes have no day count.</summary>
    public int? HoldingHorizonDays { get; set; }
    public string CreatedOn { get; set; } = default!;
    /// <summary>Fork lineage (D17).</summary>
    public string? ParentStrategyId { get; set; }
    /// <summary>candidate|live|baseline|retired|control — DEFAULT 'candidate'. No CHECK (SCHEMA).</summary>
    public string Status { get; set; } = "candidate";
}

/// <summary>accounts — one isolated book per strategy. account_id is a bare INTEGER PRIMARY KEY
/// (rowid alias, NO AUTOINCREMENT — the migration hand-edit strips the annotation, rule 14).</summary>
public sealed class AccountRow
{
    public long AccountId { get; set; }
    public string StrategyId { get; set; } = default!;
    /// <summary>decimal → TEXT (D69).</summary>
    public decimal StartingCash { get; set; }
    /// <summary>live|replay — DEFAULT 'live'. The D37 quarantine discriminant. No CHECK (SCHEMA).</summary>
    public string RunKind { get; set; } = "live";
}

/// <summary>positions — PK (account_id, security_id). Composite, so no autoincrement.</summary>
public sealed class PositionRow
{
    public long AccountId { get; set; }
    public long SecurityId { get; set; }
    /// <summary>REAL — a quantity, not money.</summary>
    public double Shares { get; set; }
    /// <summary>Raw-price basis (D30); decimal → TEXT (D69).</summary>
    public decimal CostBasis { get; set; }
    public string OpenedOn { get; set; } = default!;
    /// <summary>Fail-closed flag (D39/rule 10): valuation frozen at last print. DEFAULT 0.</summary>
    public bool Frozen { get; set; }
    public string? FrozenReason { get; set; }
}

/// <summary>
/// position_snapshots — the END-OF-DAY BOOK, one row per held name per account per session (D90).
/// PK (account_id, as_of, security_id, run_kind); run_kind is IN the key, so a replay book can never
/// overwrite the forward one (the equity_curve precedent).
///
/// WHY IT EXISTS. `positions` is current STATE, not a log: corporate actions rewrite it in place
/// (a split's share count, a merger's conversion, a spin-off's new line) through UpsertPosition, with
/// no reversible trade row. So the book as it stood at the start of a past session is not
/// recoverable from `trades` — and without it, NFR-1's "any historical run is reproducible forever"
/// (MASTER §13.5) cannot be honoured for anything the ledger touches. This table is the missing
/// as-of record: bars (D40), corporate_actions (D76) and context packs (D80) are already append-only
/// and read at a watermark; the book is the one mutable piece that was not.
///
/// It is APPEND-ONLY in the same sense equity_curve is: a row is written once per (account, session,
/// run_kind) inside that day's Stage-2 transaction and never revised afterwards. A day that rolls
/// back writes no snapshot, exactly as it writes no equity point.
///
/// The column set MIRRORS `positions` (plus the snapshot's own as_of/run_kind keys) because the whole
/// point is to restore a Position verbatim. frozen/frozen_reason are load-bearing, not decoration:
/// PortfolioPlanner short-circuits Stage 4 on a frozen position and writes FrozenReason verbatim into
/// stage_json, so a snapshot that dropped them could not reproduce a frozen-position day.
/// </summary>
public sealed class PositionSnapshotRow
{
    public long AccountId { get; set; }
    /// <summary>The session whose CLOSE this book describes.</summary>
    public string AsOf { get; set; } = default!;
    public long SecurityId { get; set; }
    /// <summary>REAL — a quantity, not money.</summary>
    public double Shares { get; set; }
    /// <summary>Raw-price basis (D30); decimal → TEXT (D69).</summary>
    public decimal CostBasis { get; set; }
    public string OpenedOn { get; set; } = default!;
    public bool Frozen { get; set; }
    public string? FrozenReason { get; set; }
    public string RunKind { get; set; } = "live";
}

/// <summary>trades — the fill log. trade_id is a bare INTEGER PRIMARY KEY (hand-edit, rule 14).
/// decided_on (close T) and filled_on (open T+1) differ by construction (MASTER §6).</summary>
public sealed class TradeRow
{
    public long TradeId { get; set; }
    public long AccountId { get; set; }
    public long SecurityId { get; set; }
    /// <summary>CHECK IN ('buy','sell') — the ONE CHECK SCHEMA declares on these eight tables.</summary>
    public string Side { get; set; } = default!;
    /// <summary>Close T.</summary>
    public string DecidedOn { get; set; } = default!;
    /// <summary>Open T+1.</summary>
    public string FilledOn { get; set; } = default!;
    public double Shares { get; set; }
    /// <summary>Raw (unadjusted) fill price (D30); decimal → TEXT (D69).</summary>
    public decimal RawFillPrice { get; set; }
    public decimal Commission { get; set; }
    public decimal SpreadCost { get; set; }
    public decimal ImpactCost { get; set; }
    /// <summary>D43 stamp — every fill stays attributable to the model that priced it.</summary>
    public string CostModelVersion { get; set; } = default!;
    /// <summary>wishlist|exit_policy|corp_action|guardrail. No CHECK (SCHEMA).</summary>
    public string Reason { get; set; } = default!;
    /// <summary>The forcing corporate action; non-null iff reason='corp_action'.</summary>
    public long? ActionId { get; set; }
    public string RunKind { get; set; } = "live";
}

/// <summary>capacity_rejections — the D43 participation-cap log. PK (account_id, security_id, as_of).
/// This table is the point of the cap: rejected quantity is logged and surfaced, never dropped.</summary>
public sealed class CapacityRejectionRow
{
    public long AccountId { get; set; }
    public long SecurityId { get; set; }
    public string AsOf { get; set; } = default!;
    public double IntendedShares { get; set; }
    public double AllowedShares { get; set; }
    /// <summary>21-day ADV in SHARES — the unit the cap and the √(Q/ADV) ratio are computed in
    /// (the spread BUCKET is the one place notional is the right unit).</summary>
    public double Adv21 { get; set; }
}

/// <summary>cash_events — non-fill cash. event_id is a bare INTEGER PRIMARY KEY (hand-edit, rule 14).</summary>
public sealed class CashEventRow
{
    public long EventId { get; set; }
    public long AccountId { get; set; }
    /// <summary>Null for account-level events (a deposit).</summary>
    public long? SecurityId { get; set; }
    /// <summary>For a dividend this is the EX-DATE (D30), not the payment date.</summary>
    public string AsOf { get; set; } = default!;
    /// <summary>dividend|merger_cash|deposit|… No CHECK (SCHEMA's list is deliberately open).</summary>
    public string Type { get; set; } = default!;
    /// <summary>decimal → TEXT (D69).</summary>
    public decimal Amount { get; set; }
    public long? ActionId { get; set; }
    public string RunKind { get; set; } = "live";
}

/// <summary>equity_curve — PK (account_id, as_of, run_kind). run_kind is IN the PK, so a replay of
/// the same day cannot overwrite the forward curve (the quarantine holds at the key level).</summary>
public sealed class EquityCurveRow
{
    public long AccountId { get; set; }
    public string AsOf { get; set; } = default!;
    /// <summary>decimal → TEXT (D69).</summary>
    public decimal Equity { get; set; }
    /// <summary>decimal → TEXT (D69).</summary>
    public decimal Cash { get; set; }
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// decisions — the "Why this trade" provenance. decision_id is a bare INTEGER PRIMARY KEY
/// (hand-edit, rule 14). stage_json is the funnel's stage-1..6 snapshot.
///
/// No FR names this table, but the funnel runs exactly once and the snapshot is UNRECOVERABLE
/// from trades afterwards — deferring it would mean an impossible backfill or a permanently blind
/// first year (NFR-2). It is also structurally load-bearing: there is no `orders` table, so the
/// decide-at-close-T / fill-at-open-T+1 split needs somewhere for T's orders to live until T+1.
/// </summary>
public sealed class DecisionRow
{
    public long DecisionId { get; set; }
    public long AccountId { get; set; }
    public string AsOf { get; set; } = default!;
    /// <summary>Funnel stages 1-6 snapshot.</summary>
    public string StageJson { get; set; } = default!;
    public string RunKind { get; set; } = "live";
}
