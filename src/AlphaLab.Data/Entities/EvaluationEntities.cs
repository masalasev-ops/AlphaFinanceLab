namespace AlphaLab.Data.Entities;

// The Phase-3 "honest arena" tables (SCHEMA_v1.9 §"CONTROL POPULATIONS", §"EVALUATION, GATE, MONITOR",
// and the D52 journal_entries under §"v1.8 ADDITIONS"). Dates/timestamps are TEXT (trading date / UTC
// ISO-8601) per SCHEMA.
//
// MONEY IS decimal (D69) — mapped to TEXT explicitly in AlphaLabDbContext. The only money column here is
// control_equity.equity. Statistics (sigma_lr, mde_ann, observed_gap_ann, overfitting_checks.value) stay
// REAL (double) — they are ratios/annualized figures, not ledger money.
//
// Following the Phase-0/1/2 precedent, the SCHEMA `REFERENCES` links (journal_entries.strategy_id →
// strategies, journal_entries.linked_entry_id → journal_entries) are documentation only — no EF
// navigations or FK constraints are declared, so no shadow indexes appear and the migration stays a
// 1:1 projection of the DDL.
//
// CHECK constraints: SCHEMA declares CHECKs on exactly TWO of these tables — overfitting_status.status
// and journal_entries.(kind, outcome). It declares NONE on control_populations, control_equity,
// trials_registry, power_reports, go_live_log, allocation_log, or overfitting_checks, so none is added
// there (the Runs_Status_IsUnconstrained precedent: the on-disk DDL matches SCHEMA exactly, no more and
// no less). overfitting_checks.signal is deliberately unconstrained: the value domain is S1..S8 plus the
// descriptive 'turnover_match' (finding 115), and SCHEMA declares no CHECK on it.

/// <summary>
/// control_populations — the random-population definitions (D36). population_id is a bare INTEGER PK
/// (rowid alias, NO AUTOINCREMENT — the migration hand-edit strips the annotation, rule 14).
/// </summary>
public sealed class ControlPopulationRow
{
    public long PopulationId { get; set; }
    /// <summary>daily|banded|monthly|quarterly.</summary>
    public string Family { get; set; } = default!;
    public int FamilySeed { get; set; }
    /// <summary>Population size M.</summary>
    public int M { get; set; }
    /// <summary>false = cost-free (display-only, never an S3 comparator). Maps to INTEGER 0/1.</summary>
    public bool CostsOn { get; set; }
    /// <summary>N, sizing, ExitPolicy shape, cost model + realized-turnover target (re-matching, finding 115).</summary>
    public string MatchedParamsJson { get; set; } = default!;
}

/// <summary>
/// control_equity — compact per-member equity curves. PK (population_id, member_index, as_of, run_kind).
/// Composite, so no autoincrement question. run_kind is IN the PK so a replay cannot overwrite the
/// forward curve (D37 quarantine at the key level, the equity_curve precedent).
/// </summary>
public sealed class ControlEquityRow
{
    public long PopulationId { get; set; }
    public int MemberIndex { get; set; }
    public string AsOf { get; set; } = default!;
    /// <summary>decimal → TEXT (D69).</summary>
    public decimal Equity { get; set; }
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// trials_registry — the honest trials count (deflated Sharpe input, S2). trial_id is a bare INTEGER PK
/// (hand-edit, rule 14). Replay trials stay separable via run_kind (D37).
/// </summary>
public sealed class TrialsRegistryRow
{
    public long TrialId { get; set; }
    public string StrategyId { get; set; } = default!;
    public string RegisteredOn { get; set; } = default!;
    /// <summary>new|fork|retrain|sibling. No CHECK (SCHEMA).</summary>
    public string Kind { get; set; } = default!;
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// power_reports — the NW-corrected MDE per pair per evaluation (D48). report_id is a bare INTEGER PK
/// (hand-edit, rule 14). sigma_lr/mde_ann/observed_gap_ann are REAL (statistics, not money).
/// </summary>
public sealed class PowerReportRow
{
    public long ReportId { get; set; }
    public string AsOf { get; set; } = default!;
    public string StrategyA { get; set; } = default!;
    public string StrategyB { get; set; } = default!;
    public int TDays { get; set; }
    /// <summary>Newey–West long-run daily σ of the paired difference series.</summary>
    public double SigmaLr { get; set; }
    /// <summary>Bartlett lag L actually used (= min(2·maxHorizon, NwLagCapDays)).</summary>
    public int NwLag { get; set; }
    /// <summary>Annualized minimum detectable effect at the configured confidence/power.</summary>
    public double MdeAnn { get; set; }
    /// <summary>Observed annualized A−B gap; null when not yet computable.</summary>
    public double? ObservedGapAnn { get; set; }
    /// <summary>Promoted|Refused|TooEarly (nullable — power_reports may record a pair with no gate verdict).</summary>
    public string? Verdict { get; set; }
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// go_live_log — the promotion/demotion audit (D31). event_id is a bare INTEGER PK (hand-edit, rule 14).
/// verdict is Promoted|Refused|TooEarly|Revert. No CHECK (SCHEMA).
/// </summary>
public sealed class GoLiveLogRow
{
    public long EventId { get; set; }
    public string AsOf { get; set; } = default!;
    /// <summary>strategy_id promoted this event; null when none.</summary>
    public string? Promoted { get; set; }
    /// <summary>strategy_id demoted this event; null when none.</summary>
    public string? Demoted { get; set; }
    public string Verdict { get; set; } = default!;
    public string EvidenceJson { get; set; } = default!;
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// allocation_log — the ensemble allocator's full reconstructible input vector (D51/FR-27, NFR-2).
/// event_id is a bare INTEGER PK (hand-edit, rule 14).
/// </summary>
public sealed class AllocationLogRow
{
    public long EventId { get; set; }
    public string AsOf { get; set; } = default!;
    /// <summary>The per-strategy {α̂, se, α̃, w, target, applied, clamps_bound} vector.</summary>
    public string WeightsJson { get; set; } = default!;
    public string Reason { get; set; } = default!;
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// overfitting_checks — one row per signal per strategy per evaluation (the S1..S8 monitor rows plus the
/// descriptive 'turnover_match' row, finding 115). check_id is a bare INTEGER PK (hand-edit, rule 14).
/// value is REAL (nullable). The persisted signal='S3' path is read back by the FR-35 separation state
/// and the FR-39 cohort curve (covering index ix_overfitting_checks_path).
/// </summary>
public sealed class OverfittingCheckRow
{
    public long CheckId { get; set; }
    public string StrategyId { get; set; } = default!;
    public string AsOf { get; set; } = default!;
    /// <summary>S1..S8; or 'turnover_match' (descriptive, excluded from overfitting_status). No CHECK (SCHEMA).</summary>
    public string Signal { get; set; } = default!;
    public double? Value { get; set; }
    public string ThresholdJson { get; set; } = default!;
    /// <summary>The signal's contribution to the aggregate status (e.g. none|elevated|critical);
    /// 'turnover_match' rows carry a neutral contribution so they never move the verdict.</summary>
    public string Contribution { get; set; } = default!;
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// overfitting_status — the aggregate monitor verdict transitions. PK (strategy_id, as_of, run_kind).
/// Composite, so no autoincrement question. status carries the one CHECK on this table.
/// </summary>
public sealed class OverfittingStatusRow
{
    public string StrategyId { get; set; } = default!;
    public string AsOf { get; set; } = default!;
    /// <summary>CHECK IN ('healthy','warning','suspect','retired') — the one CHECK on this table.</summary>
    public string Status { get; set; } = default!;
    public string TriggerJson { get; set; } = default!;
    public string RunKind { get; set; } = "live";
}

/// <summary>
/// journal_entries — the D52 pre-registration / operator-learning journal. entry_id is a bare INTEGER PK
/// (hand-edit, rule 14). kind and outcome carry CHECKs (SCHEMA). A locked hypothesis row is immutable
/// except via the outcome-closure flow; CandidateFactory requires a linked hypothesis OR an
/// 'unregistered' marker in strategies.config_json (rule 16).
/// </summary>
public sealed class JournalEntryRow
{
    public long EntryId { get; set; }
    public string CreatedOn { get; set; } = default!;
    /// <summary>CHECK IN ('hypothesis','observation','decision_note','skeptic_review','outcome').</summary>
    public string Kind { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string BodyMd { get; set; } = default!;
    /// <summary>Documentary link to strategies (no EF FK).</summary>
    public string? StrategyId { get; set; }
    /// <summary>outcome → hypothesis link (no EF FK).</summary>
    public long? LinkedEntryId { get; set; }
    /// <summary>Pre-declared confirm/refute metric (a hypothesis's frozen claim).</summary>
    public string? Metric { get; set; }
    /// <summary>Pre-declared evidence window (days).</summary>
    public int? EvidenceWindowDays { get; set; }
    /// <summary>CHECK IN ('confirmed','refuted','inconclusive') — nullable until closed.</summary>
    public string? Outcome { get; set; }
    /// <summary>true (INTEGER 1) once linked at candidate creation; immutable thereafter. DEFAULT 0.</summary>
    public bool Locked { get; set; }
}
