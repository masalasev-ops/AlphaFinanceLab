namespace AlphaLab.Data.Entities;

// Phase 1 data-domain tables (SCHEMA_v1.9 §"Identity & Market Data" + §"v1.8 additions"). Nine
// tables land here; regime_labels/regime_episodes/features/factor_* and the ux_runs_ok_forward
// index are deferred to Phase 2. Timestamps + dates are TEXT (UTC ISO-8601 / trading date) per
// SCHEMA. Market-data prices/derived stats are REAL (double); ledger money is decimal→TEXT (D69).
// Column/table names are mapped snake_case in AlphaLabDbContext.OnModelCreating. Following the
// Phase-0 precedent (catchup_log.run_id, worker_state.current_run_id), the SCHEMA `REFERENCES`
// links are documentation only — no EF navigations / FK constraints are declared, so no shadow
// indexes appear and the migration stays a 1:1 projection of the DDL.

/// <summary>securities — permanent identity (D39). security_id is a bare INTEGER PRIMARY KEY
/// (rowid alias, NO AUTOINCREMENT — the migration hand-edit strips the annotation, rule 14).</summary>
public sealed class SecurityRow
{
    public long SecurityId { get; set; }
    public string CurrentSymbol { get; set; } = default!;
    public string? Name { get; set; }
    public string? Exchange { get; set; }
    public string? Sector { get; set; }
    public string? Industry { get; set; }
    public string FirstSeen { get; set; } = default!;
    /// <summary>Terminal only (no successor). NULL = active. Drives ux_securities_active_symbol.</summary>
    public string? DelistedOn { get; set; }
}

/// <summary>ticker_history — time-ranged symbol aliases (D39). PK (security_id, valid_from).</summary>
public sealed class TickerHistoryRow
{
    public long SecurityId { get; set; }
    public string Symbol { get; set; } = default!;
    public string ValidFrom { get; set; } = default!;
    /// <summary>NULL = current.</summary>
    public string? ValidTo { get; set; }
}

/// <summary>sector_changes — classification change log (D35). PK (security_id, changed_on).</summary>
public sealed class SectorChangeRow
{
    public long SecurityId { get; set; }
    public string ChangedOn { get; set; } = default!;
    public string? OldSector { get; set; }
    public string? NewSector { get; set; }
    public string? OldIndustry { get; set; }
    public string? NewIndustry { get; set; }
}

/// <summary>
/// bars — versioned append-only market data (D40). PK (security_id, date, version). Never
/// UPDATE/DELETE (rule 3, ci.ps1 grep). Raw OHLCV + adj_close only; EODHD supplies no adjusted
/// OHL so adj_open/adj_high/adj_low stay NULL. Prices/volume are REAL/INTEGER (market data, D69).
/// READ RULE: latest version WHERE observed_at &lt;= run.watermark.
/// </summary>
public sealed class BarRow
{
    public long SecurityId { get; set; }
    public string Date { get; set; } = default!;
    /// <summary>NOT NULL DEFAULT 1; correction inserts version = MAX(version)+1.</summary>
    public int Version { get; set; }
    /// <summary>When WE first saw this version (the point-in-time key).</summary>
    public string ObservedAt { get; set; } = default!;
    public double? Open { get; set; }
    public double? High { get; set; }
    public double? Low { get; set; }
    public double? Close { get; set; }
    public long? Volume { get; set; }
    public double? AdjOpen { get; set; }
    public double? AdjHigh { get; set; }
    public double? AdjLow { get; set; }
    public double? AdjClose { get; set; }
    public string Source { get; set; } = "eodhd";
}

/// <summary>
/// corporate_actions — §13.6 semantics. action_id is a bare INTEGER PRIMARY KEY (NO AUTOINCREMENT,
/// rule 14 hand-edit). type is CHECK-constrained (8 values). cash_per_share is decimal→TEXT (D69);
/// ratio is REAL. processed_on stays NULL until the ledger applies it (Phase 2).
/// </summary>
public sealed class CorporateActionRow
{
    public long ActionId { get; set; }
    public long SecurityId { get; set; }
    /// <summary>CHECK IN ('dividend','split','ticker_change','merger_cash','merger_stock','merger_mixed','spinoff','delist').</summary>
    public string Type { get; set; } = default!;
    /// <summary>Ex-date (dividends).</summary>
    public string? ExDate { get; set; }
    public string EffectiveDate { get; set; } = default!;
    /// <summary>Dividend / merger cash leg — C# decimal persisted as TEXT (D69).</summary>
    public decimal? CashPerShare { get; set; }
    /// <summary>Split / exchange / spinoff ratio (REAL).</summary>
    public double? Ratio { get; set; }
    public long? CounterpartySecurityId { get; set; }
    /// <summary>ticker_change new symbol.</summary>
    public string? NewSymbol { get; set; }
    public string ObservedAt { get; set; } = default!;
    public string Source { get; set; } = "eodhd";
    /// <summary>NULL until the ledger applies the action (Phase 2).</summary>
    public string? ProcessedOn { get; set; }
}

/// <summary>
/// index_membership_log — D35 daily refresh audit. log_id is a bare INTEGER PRIMARY KEY
/// (NO AUTOINCREMENT, rule 14 hand-edit). agreed is 0/1.
/// </summary>
public sealed class IndexMembershipLogRow
{
    public long LogId { get; set; }
    public string AsOf { get; set; } = default!;
    public int? SourceCount { get; set; }
    public int? CrosscheckCount { get; set; }
    /// <summary>0/1.</summary>
    public int Agreed { get; set; }
    /// <summary>Applied diff (security_ids) as JSON.</summary>
    public string? AddsJson { get; set; }
    public string? DropsJson { get; set; }
    public string? Note { get; set; }
}

/// <summary>index_membership — as-of state, never deleted (D35/D70). PK (security_id, added_on);
/// removed_on NULL = currently in index.</summary>
public sealed class IndexMembershipRow
{
    public long SecurityId { get; set; }
    public string AddedOn { get; set; } = default!;
    /// <summary>NULL = currently in index.</summary>
    public string? RemovedOn { get; set; }
}

/// <summary>trading_calendar — sessions only (D54). PK date; session CHECK IN ('full','half');
/// close_time_local is ET close, e.g. '16:00' / '13:00'.</summary>
public sealed class TradingCalendarRow
{
    public string Date { get; set; } = default!;
    /// <summary>CHECK IN ('full','half').</summary>
    public string Session { get; set; } = default!;
    public string CloseTimeLocal { get; set; } = default!;
}

/// <summary>api_usage_log — INTEGRATIONS headroom rule (FR-6). PK (as_of, source).</summary>
public sealed class ApiUsageLogRow
{
    public string AsOf { get; set; } = default!;
    public string Source { get; set; } = default!;
    public int Calls { get; set; }
    public int? PlanLimit { get; set; }
}
