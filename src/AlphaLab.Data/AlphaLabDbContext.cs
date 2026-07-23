using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data;

/// <summary>
/// The single EF Core context over the arena's SQLite file. Phase 0 mapped the five
/// infrastructure tables (runs, catchup_log, config, worker_state, jobs); Phase 1 adds the nine
/// data-domain tables (securities, ticker_history, sector_changes, bars, corporate_actions,
/// index_membership_log, index_membership, trading_calendar, api_usage_log). All names are
/// snake_case to match SCHEMA_v1.9 exactly.
/// </summary>
public sealed class AlphaLabDbContext(DbContextOptions<AlphaLabDbContext> options) : DbContext(options)
{
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<CatchupLogRow> CatchupLog => Set<CatchupLogRow>();
    public DbSet<ConfigRow> Config => Set<ConfigRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<WorkerStateRow> WorkerState => Set<WorkerStateRow>();

    // ---- Phase 1 data-domain tables ----
    public DbSet<SecurityRow> Securities => Set<SecurityRow>();
    public DbSet<TickerHistoryRow> TickerHistory => Set<TickerHistoryRow>();
    public DbSet<SectorChangeRow> SectorChanges => Set<SectorChangeRow>();
    public DbSet<BarRow> Bars => Set<BarRow>();
    public DbSet<CorporateActionRow> CorporateActions => Set<CorporateActionRow>();
    public DbSet<IndexMembershipLogRow> IndexMembershipLog => Set<IndexMembershipLogRow>();
    public DbSet<IndexMembershipRow> IndexMembership => Set<IndexMembershipRow>();
    public DbSet<TradingCalendarRow> TradingCalendar => Set<TradingCalendarRow>();
    public DbSet<ApiUsageLogRow> ApiUsageLog => Set<ApiUsageLogRow>();
    public DbSet<DataQualityFlagRow> DataQualityFlags => Set<DataQualityFlagRow>();

    // ---- Phase 2 regime tables (D34/D45/D50) ----
    public DbSet<RegimeLabelRow> RegimeLabels => Set<RegimeLabelRow>();
    public DbSet<RegimeEpisodeRow> RegimeEpisodes => Set<RegimeEpisodeRow>();

    // ---- Phase 2 ledger tables (D29/D30/D43; money is decimal → TEXT per D69) ----
    public DbSet<StrategyRow> Strategies => Set<StrategyRow>();
    public DbSet<AccountRow> Accounts => Set<AccountRow>();
    public DbSet<PositionRow> Positions => Set<PositionRow>();
    public DbSet<PositionSnapshotRow> PositionSnapshots => Set<PositionSnapshotRow>();
    public DbSet<TradeRow> Trades => Set<TradeRow>();
    public DbSet<CapacityRejectionRow> CapacityRejections => Set<CapacityRejectionRow>();
    public DbSet<CashEventRow> CashEvents => Set<CashEventRow>();
    public DbSet<EquityCurveRow> EquityCurve => Set<EquityCurveRow>();
    public DbSet<DecisionRow> Decisions => Set<DecisionRow>();

    // ---- Phase 3 "honest arena" tables (D36/D48/D51/D52; MONITOR doc) ----
    public DbSet<ControlPopulationRow> ControlPopulations => Set<ControlPopulationRow>();
    public DbSet<ControlEquityRow> ControlEquity => Set<ControlEquityRow>();
    public DbSet<TrialsRegistryRow> TrialsRegistry => Set<TrialsRegistryRow>();
    public DbSet<PowerReportRow> PowerReports => Set<PowerReportRow>();
    public DbSet<GoLiveLogRow> GoLiveLog => Set<GoLiveLogRow>();
    public DbSet<AllocationLogRow> AllocationLog => Set<AllocationLogRow>();
    public DbSet<OverfittingCheckRow> OverfittingChecks => Set<OverfittingCheckRow>();
    public DbSet<OverfittingStatusRow> OverfittingStatus => Set<OverfittingStatusRow>();
    public DbSet<JournalEntryRow> JournalEntries => Set<JournalEntryRow>();

    // ---- Phase 4 replay table (D89/FR-41; M5) ----
    public DbSet<ReplayRegimeOutcomeRow> ReplayRegimeOutcomes => Set<ReplayRegimeOutcomeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- runs ----
        modelBuilder.Entity<RunRow>(e =>
        {
            e.ToTable("runs", t =>
                t.HasCheckConstraint("ck_runs_run_kind", "run_kind IN ('live','catchup','replay')"));
            e.HasKey(x => x.RunId);
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired();
            e.Property(x => x.Watermark).HasColumnName("watermark").IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            // status: defaulted but UNCONSTRAINED — no CHECK (SCHEMA fidelity).
            e.Property(x => x.Status).HasColumnName("status").IsRequired().HasDefaultValue("running");
            e.Property(x => x.InputsHash).HasColumnName("inputs_hash");
            // Forward-run uniqueness (v1.9.7 finding 109; SCHEMA:341-348, M3/checkpoint 2.10). At most ONE
            // status='ok' row per as_of among FORWARD kinds — a PARTIAL unique index, not a plain unique(as_of):
            // failed runs legitimately retry (a second row, same as_of) and replay produces many runs over the
            // same historical date by design, so both are exempt by the filter. This is what makes catch-up
            // idempotency ("re-running a recovered day is a no-op") and catchup_log(as_of PK) mutually
            // consistent. Placed here (M3) because SCHEMA:344 says it is "created when Stage-2 first writes runs".
            e.HasIndex(x => x.AsOf)
                .HasDatabaseName("ux_runs_ok_forward")
                .IsUnique()
                .HasFilter("status = 'ok' AND run_kind IN ('live','catchup')");
        });

        // ---- catchup_log ----
        modelBuilder.Entity<CatchupLogRow>(e =>
        {
            e.ToTable("catchup_log");
            e.HasKey(x => x.AsOf);
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.RecoveredAt).HasColumnName("recovered_at").IsRequired();
            e.Property(x => x.RunId).HasColumnName("run_id").IsRequired();
        });

        // ---- config ---- composite PK (key, version); version writer-supplied (finding 108 / D56).
        modelBuilder.Entity<ConfigRow>(e =>
        {
            e.ToTable("config");
            e.HasKey(x => new { x.Key, x.Version });
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.ValueJson).HasColumnName("value_json").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").ValueGeneratedNever();
            e.Property(x => x.ChangedOn).HasColumnName("changed_on").IsRequired();
            e.Property(x => x.Reason).HasColumnName("reason");
        });

        // ---- jobs ----
        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("jobs", t =>
            {
                t.HasCheckConstraint("ck_jobs_kind", "kind IN ('replay','analysis_brief','analysis_skeptic')");
                t.HasCheckConstraint("ck_jobs_status", "status IN ('queued','running','done','failed')");
            });
            e.HasKey(x => x.JobId);
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").IsRequired().HasDefaultValue("queued");
            e.Property(x => x.SubmittedAt).HasColumnName("submitted_at").IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.RequestJson).HasColumnName("request_json").IsRequired();
            e.Property(x => x.ResultRef).HasColumnName("result_ref");
            e.Property(x => x.ErrorJson).HasColumnName("error_json");
        });

        // ---- worker_state ---- single row (CHECK id = 1), seeded here.
        modelBuilder.Entity<WorkerStateRow>(e =>
        {
            e.ToTable("worker_state", t => t.HasCheckConstraint("ck_worker_state_id", "id = 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.RunInProgress).HasColumnName("run_in_progress").IsRequired().HasDefaultValue(0);
            e.Property(x => x.CurrentRunId).HasColumnName("current_run_id");
            e.Property(x => x.HeartbeatAt).HasColumnName("heartbeat_at");
            // Seed the single row (id=1) — emitted as InsertData in InitialCreate, no hand-edit (rule 14).
            e.HasData(new WorkerStateRow { Id = 1, RunInProgress = 0, CurrentRunId = null, HeartbeatAt = null });
        });

        // ================= Phase 1 data-domain tables (SCHEMA §Identity & Market Data / §v1.8) ==

        // ---- securities ---- security_id is a bare INTEGER PK (NO AUTOINCREMENT — migration hand-edit).
        modelBuilder.Entity<SecurityRow>(e =>
        {
            e.ToTable("securities");
            e.HasKey(x => x.SecurityId);
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.CurrentSymbol).HasColumnName("current_symbol").IsRequired();
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Exchange).HasColumnName("exchange");
            e.Property(x => x.Sector).HasColumnName("sector");
            e.Property(x => x.Industry).HasColumnName("industry");
            e.Property(x => x.FirstSeen).HasColumnName("first_seen").IsRequired();
            e.Property(x => x.DelistedOn).HasColumnName("delisted_on");
            // Partial unique index: symbol uniqueness holds only among ACTIVE listings (D39).
            e.HasIndex(x => new { x.CurrentSymbol, x.Exchange })
                .HasDatabaseName("ux_securities_active_symbol")
                .IsUnique()
                .HasFilter("delisted_on IS NULL");
        });

        // ---- ticker_history ---- PK (security_id, valid_from).
        modelBuilder.Entity<TickerHistoryRow>(e =>
        {
            e.ToTable("ticker_history");
            e.HasKey(x => new { x.SecurityId, x.ValidFrom });
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.Symbol).HasColumnName("symbol").IsRequired();
            e.Property(x => x.ValidFrom).HasColumnName("valid_from");
            e.Property(x => x.ValidTo).HasColumnName("valid_to");
            e.HasIndex(x => new { x.Symbol, x.ValidFrom }).HasDatabaseName("ix_ticker_hist_symbol");
        });

        // ---- sector_changes ---- PK (security_id, changed_on).
        modelBuilder.Entity<SectorChangeRow>(e =>
        {
            e.ToTable("sector_changes");
            e.HasKey(x => new { x.SecurityId, x.ChangedOn });
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.ChangedOn).HasColumnName("changed_on");
            e.Property(x => x.OldSector).HasColumnName("old_sector");
            e.Property(x => x.NewSector).HasColumnName("new_sector");
            e.Property(x => x.OldIndustry).HasColumnName("old_industry");
            e.Property(x => x.NewIndustry).HasColumnName("new_industry");
        });

        // ---- bars ---- versioned append-only; PK (security_id, date, version). Never UPDATE/DELETE.
        modelBuilder.Entity<BarRow>(e =>
        {
            e.ToTable("bars");
            e.HasKey(x => new { x.SecurityId, x.Date, x.Version });
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Version).HasColumnName("version").IsRequired().HasDefaultValue(1);
            e.Property(x => x.ObservedAt).HasColumnName("observed_at").IsRequired();
            e.Property(x => x.Open).HasColumnName("open");
            e.Property(x => x.High).HasColumnName("high");
            e.Property(x => x.Low).HasColumnName("low");
            e.Property(x => x.Close).HasColumnName("close");
            e.Property(x => x.Volume).HasColumnName("volume");
            e.Property(x => x.AdjOpen).HasColumnName("adj_open");
            e.Property(x => x.AdjHigh).HasColumnName("adj_high");
            e.Property(x => x.AdjLow).HasColumnName("adj_low");
            e.Property(x => x.AdjClose).HasColumnName("adj_close");
            e.Property(x => x.Source).HasColumnName("source").IsRequired().HasDefaultValue("eodhd");
            e.HasIndex(x => x.ObservedAt).HasDatabaseName("ix_bars_observed");
            // Date-major (cross-sectional) reads — "every name at date D" (Phase-2 funnel / Phase-4
            // replay). Without this a WHERE date = ? (no security_id) full-scans bars (D78).
            e.HasIndex(x => x.Date).HasDatabaseName("ix_bars_date");
        });

        // ---- corporate_actions ---- action_id bare INTEGER PK (NO AUTOINCREMENT); 8-value type CHECK.
        // Versioned append-only like bars (D76): observed_at is the point-in-time key; ux_..._identity
        // enforces one row per (security_id, type, effective_date, version). ex_date is EXCLUDED from the
        // identity index (splits carry NULL ex_date and SQLite treats NULLs as distinct, so it would not
        // dedupe; effective_date is NOT NULL and, for dividends, equals ex_date).
        modelBuilder.Entity<CorporateActionRow>(e =>
        {
            e.ToTable("corporate_actions", t => t.HasCheckConstraint(
                "ck_corporate_actions_type",
                "type IN ('dividend','split','ticker_change','merger_cash','merger_stock','merger_mixed','spinoff','delist')"));
            e.HasKey(x => x.ActionId);
            e.Property(x => x.ActionId).HasColumnName("action_id");
            e.Property(x => x.SecurityId).HasColumnName("security_id").IsRequired();
            e.Property(x => x.Type).HasColumnName("type").IsRequired();
            e.Property(x => x.ExDate).HasColumnName("ex_date");
            e.Property(x => x.EffectiveDate).HasColumnName("effective_date").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsRequired().HasDefaultValue(1);
            // decimal → TEXT (D69). EF's default SQLite decimal mapping is TEXT; declared explicitly.
            e.Property(x => x.CashPerShare).HasColumnName("cash_per_share").HasColumnType("TEXT");
            e.Property(x => x.Ratio).HasColumnName("ratio");
            e.Property(x => x.CounterpartySecurityId).HasColumnName("counterparty_security_id");
            e.Property(x => x.NewSymbol).HasColumnName("new_symbol");
            e.Property(x => x.ObservedAt).HasColumnName("observed_at").IsRequired();
            e.Property(x => x.Source).HasColumnName("source").IsRequired().HasDefaultValue("eodhd");
            // processed_on dropped by D94/M5 (was ALWAYS NULL, never written — proposal P5 resolved).
            e.HasIndex(x => x.ObservedAt).HasDatabaseName("ix_corporate_actions_observed");
            e.HasIndex(x => new { x.SecurityId, x.Type, x.EffectiveDate, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_corporate_actions_identity");
        });

        // ---- index_membership_log ---- log_id bare INTEGER PK (NO AUTOINCREMENT).
        modelBuilder.Entity<IndexMembershipLogRow>(e =>
        {
            e.ToTable("index_membership_log");
            e.HasKey(x => x.LogId);
            e.Property(x => x.LogId).HasColumnName("log_id");
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.SourceCount).HasColumnName("source_count");
            e.Property(x => x.CrosscheckCount).HasColumnName("crosscheck_count");
            e.Property(x => x.Agreed).HasColumnName("agreed").IsRequired();
            e.Property(x => x.AddsJson).HasColumnName("adds_json");
            e.Property(x => x.DropsJson).HasColumnName("drops_json");
            e.Property(x => x.Note).HasColumnName("note");
        });

        // ---- index_membership ---- as-of state; PK (security_id, added_on).
        modelBuilder.Entity<IndexMembershipRow>(e =>
        {
            e.ToTable("index_membership");
            e.HasKey(x => new { x.SecurityId, x.AddedOn });
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.AddedOn).HasColumnName("added_on");
            e.Property(x => x.RemovedOn).HasColumnName("removed_on");
        });

        // ---- trading_calendar ---- PK date; session CHECK IN ('full','half').
        modelBuilder.Entity<TradingCalendarRow>(e =>
        {
            e.ToTable("trading_calendar", t => t.HasCheckConstraint(
                "ck_trading_calendar_session", "session IN ('full','half')"));
            e.HasKey(x => x.Date);
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Session).HasColumnName("session").IsRequired();
            e.Property(x => x.CloseTimeLocal).HasColumnName("close_time_local").IsRequired();
        });

        // ---- api_usage_log ---- PK (as_of, source).
        modelBuilder.Entity<ApiUsageLogRow>(e =>
        {
            e.ToTable("api_usage_log");
            e.HasKey(x => new { x.AsOf, x.Source });
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.Calls).HasColumnName("calls").IsRequired();
            e.Property(x => x.PlanLimit).HasColumnName("plan_limit");
        });

        // ---- data_quality_flags (D77) ---- flag_id bare INTEGER PK (NO AUTOINCREMENT); issue + severity CHECKs.
        modelBuilder.Entity<DataQualityFlagRow>(e =>
        {
            e.ToTable("data_quality_flags", t =>
            {
                t.HasCheckConstraint("ck_data_quality_flags_issue",
                    "issue IN ('missing_bar','nan_field','non_positive_price','outlier_return','unexplained_adjustment','cross_check_mismatch')");
                t.HasCheckConstraint("ck_data_quality_flags_severity", "severity IN ('warn','reject')");
            });
            e.HasKey(x => x.FlagId);
            e.Property(x => x.FlagId).HasColumnName("flag_id");
            e.Property(x => x.RunId).HasColumnName("run_id").IsRequired();
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.Symbol).HasColumnName("symbol").IsRequired();
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Issue).HasColumnName("issue").IsRequired();
            e.Property(x => x.Severity).HasColumnName("severity").IsRequired();
            e.Property(x => x.Detail).HasColumnName("detail").IsRequired();
            e.Property(x => x.ObservedAt).HasColumnName("observed_at").IsRequired();
            e.HasIndex(x => x.RunId).HasDatabaseName("ix_data_quality_flags_run");
        });

        // ---- regime_labels (D34/D50; D93/M5) ---- PK (as_of, run_kind); trend + vol CHECKs. Derived PIT
        // table, no version (inputs_hash carries the watermark provenance). run_kind is IN the key so a
        // replay recompute of a historical session cannot overwrite the forward label (P6 resolved — the
        // equity_curve precedent).
        modelBuilder.Entity<RegimeLabelRow>(e =>
        {
            e.ToTable("regime_labels", t =>
            {
                t.HasCheckConstraint("ck_regime_labels_trend", "trend IN ('bull','bear')");
                t.HasCheckConstraint("ck_regime_labels_vol", "vol IN ('normal_vol','high_vol')");
            });
            e.HasKey(x => new { x.AsOf, x.RunKind });
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.Trend).HasColumnName("trend").IsRequired();
            e.Property(x => x.Vol).HasColumnName("vol").IsRequired();
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.InputsHash).HasColumnName("inputs_hash").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").HasDefaultValue("live");
        });

        // ---- regime_episodes (D45; D93/M5) ---- episode_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit).
        // No CHECK (SCHEMA declares none; label reuses the trend tokens but is unconstrained here, the
        // trades.reason precedent). end_date nullable = ongoing. run_kind: each run kind maintains its own
        // episode chain (a replay's chain over its window never touches the forward chain); the
        // (run_kind, start_date) index serves the per-kind latest-episode read and FR-41's per-episode joins.
        modelBuilder.Entity<RegimeEpisodeRow>(e =>
        {
            e.ToTable("regime_episodes");
            e.HasKey(x => x.EpisodeId);
            e.Property(x => x.EpisodeId).HasColumnName("episode_id");
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.StartDate).HasColumnName("start_date").IsRequired();
            e.Property(x => x.EndDate).HasColumnName("end_date");
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
            e.HasIndex(x => new { x.RunKind, x.StartDate }).HasDatabaseName("ix_regime_episodes_kind_start");
        });

        // ================= Phase 2: the ledger (SCHEMA §"STRATEGIES, ACCOUNTS, LEDGER") =========
        // Money → TEXT is declared EXPLICITLY on every money column (D69). EF's default SQLite
        // decimal mapping is already TEXT, but stating it means a future provider change or a
        // convention tweak cannot silently demote the ledger to REAL.
        //
        // Exactly ONE CHECK across these eight tables — trades.side. SCHEMA declares no CHECK on
        // strategies.status, accounts.run_kind, cash_events.type, or trades.reason; adding one
        // would make the on-disk DDL diverge from the single source of truth.

        // ---- strategies ---- TEXT PK (no rowid, so no autoincrement question).
        modelBuilder.Entity<StrategyRow>(e =>
        {
            e.ToTable("strategies");
            e.HasKey(x => x.StrategyId);
            e.Property(x => x.StrategyId).HasColumnName("strategy_id");
            e.Property(x => x.Family).HasColumnName("family").IsRequired();
            e.Property(x => x.ConfigJson).HasColumnName("config_json").IsRequired();
            e.Property(x => x.ExitPolicyJson).HasColumnName("exit_policy_json").IsRequired();
            e.Property(x => x.HoldingHorizonDays).HasColumnName("holding_horizon_days");
            e.Property(x => x.CreatedOn).HasColumnName("created_on").IsRequired();
            e.Property(x => x.ParentStrategyId).HasColumnName("parent_strategy_id");
            // status: defaulted but UNCONSTRAINED — no CHECK (SCHEMA fidelity).
            e.Property(x => x.Status).HasColumnName("status").IsRequired().HasDefaultValue("candidate");
        });

        // ---- accounts ----
        modelBuilder.Entity<AccountRow>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.AccountId);
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.StrategyId).HasColumnName("strategy_id").IsRequired();
            e.Property(x => x.StartingCash).HasColumnName("starting_cash").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- positions ---- PK (account_id, security_id).
        modelBuilder.Entity<PositionRow>(e =>
        {
            e.ToTable("positions");
            e.HasKey(x => new { x.AccountId, x.SecurityId });
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.Shares).HasColumnName("shares").IsRequired();   // REAL — a quantity
            e.Property(x => x.CostBasis).HasColumnName("cost_basis").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.OpenedOn).HasColumnName("opened_on").IsRequired();
            e.Property(x => x.Frozen).HasColumnName("frozen").IsRequired().HasDefaultValue(false);
            e.Property(x => x.FrozenReason).HasColumnName("frozen_reason");
        });

        // ---- position_snapshots ---- PK (account_id, as_of, security_id, run_kind): run_kind is IN
        // the key, so a replay book cannot overwrite the forward one (the equity_curve precedent, D37).
        modelBuilder.Entity<PositionSnapshotRow>(e =>
        {
            e.ToTable("position_snapshots");
            e.HasKey(x => new { x.AccountId, x.AsOf, x.SecurityId, x.RunKind });
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.Shares).HasColumnName("shares").IsRequired();   // REAL — a quantity
            e.Property(x => x.CostBasis).HasColumnName("cost_basis").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.OpenedOn).HasColumnName("opened_on").IsRequired();
            e.Property(x => x.Frozen).HasColumnName("frozen").IsRequired().HasDefaultValue(false);
            e.Property(x => x.FrozenReason).HasColumnName("frozen_reason");
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- trades ---- the one CHECK.
        modelBuilder.Entity<TradeRow>(e =>
        {
            e.ToTable("trades", t =>
                t.HasCheckConstraint("ck_trades_side", "side IN ('buy','sell')"));
            e.HasKey(x => x.TradeId);
            e.Property(x => x.TradeId).HasColumnName("trade_id");
            e.Property(x => x.AccountId).HasColumnName("account_id").IsRequired();
            e.Property(x => x.SecurityId).HasColumnName("security_id").IsRequired();
            e.Property(x => x.Side).HasColumnName("side").IsRequired();
            e.Property(x => x.DecidedOn).HasColumnName("decided_on").IsRequired();
            e.Property(x => x.FilledOn).HasColumnName("filled_on").IsRequired();
            e.Property(x => x.Shares).HasColumnName("shares").IsRequired();   // REAL — a quantity
            e.Property(x => x.RawFillPrice).HasColumnName("raw_fill_price").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Commission).HasColumnName("commission").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.SpreadCost).HasColumnName("spread_cost").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.ImpactCost).HasColumnName("impact_cost").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.CostModelVersion).HasColumnName("cost_model_version").IsRequired();
            e.Property(x => x.Reason).HasColumnName("reason").IsRequired();
            e.Property(x => x.ActionId).HasColumnName("action_id");
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- capacity_rejections ---- PK (account_id, security_id, as_of).
        modelBuilder.Entity<CapacityRejectionRow>(e =>
        {
            e.ToTable("capacity_rejections");
            e.HasKey(x => new { x.AccountId, x.SecurityId, x.AsOf });
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.IntendedShares).HasColumnName("intended_shares");
            e.Property(x => x.AllowedShares).HasColumnName("allowed_shares");
            e.Property(x => x.Adv21).HasColumnName("adv21");
        });

        // ---- cash_events ---- type is UNCONSTRAINED (SCHEMA's list is deliberately open-ended).
        modelBuilder.Entity<CashEventRow>(e =>
        {
            e.ToTable("cash_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.AccountId).HasColumnName("account_id").IsRequired();
            e.Property(x => x.SecurityId).HasColumnName("security_id");
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.Type).HasColumnName("type").IsRequired();
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.ActionId).HasColumnName("action_id");
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- equity_curve ---- PK (account_id, as_of, run_kind): run_kind is IN the key, so a
        // replay of the same day cannot overwrite the forward curve (D37 quarantine at key level).
        modelBuilder.Entity<EquityCurveRow>(e =>
        {
            e.ToTable("equity_curve");
            e.HasKey(x => new { x.AccountId, x.AsOf, x.RunKind });
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.Equity).HasColumnName("equity").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Cash).HasColumnName("cash").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").HasDefaultValue("live");
        });

        // ---- decisions ----
        modelBuilder.Entity<DecisionRow>(e =>
        {
            e.ToTable("decisions");
            e.HasKey(x => x.DecisionId);
            e.Property(x => x.DecisionId).HasColumnName("decision_id");
            e.Property(x => x.AccountId).HasColumnName("account_id").IsRequired();
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.StageJson).HasColumnName("stage_json").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ================= Phase 3: the honest arena (SCHEMA §"CONTROL POPULATIONS", §"EVALUATION,
        // GATE, MONITOR", §"v1.8 ADDITIONS" journal_entries) ====================================
        // Money → TEXT is declared explicitly on the one money column (control_equity.equity, D69).
        // Exactly TWO CHECKs across these nine tables — overfitting_status.status and
        // journal_entries.(kind, outcome); SCHEMA declares no CHECK on the other seven, so none is added.

        // ---- control_populations ---- population_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit).
        modelBuilder.Entity<ControlPopulationRow>(e =>
        {
            e.ToTable("control_populations");
            e.HasKey(x => x.PopulationId);
            e.Property(x => x.PopulationId).HasColumnName("population_id");
            e.Property(x => x.Family).HasColumnName("family").IsRequired();
            e.Property(x => x.FamilySeed).HasColumnName("family_seed").IsRequired();
            e.Property(x => x.M).HasColumnName("m").IsRequired();
            e.Property(x => x.CostsOn).HasColumnName("costs_on").IsRequired();
            e.Property(x => x.MatchedParamsJson).HasColumnName("matched_params_json").IsRequired();
        });

        // ---- control_equity ---- PK (population_id, member_index, as_of, run_kind): run_kind IN the key
        // quarantines a replay curve from the forward one (D37, the equity_curve precedent).
        modelBuilder.Entity<ControlEquityRow>(e =>
        {
            e.ToTable("control_equity");
            e.HasKey(x => new { x.PopulationId, x.MemberIndex, x.AsOf, x.RunKind });
            e.Property(x => x.PopulationId).HasColumnName("population_id");
            e.Property(x => x.MemberIndex).HasColumnName("member_index");
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.Equity).HasColumnName("equity").HasColumnType("TEXT").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").HasDefaultValue("live");
        });

        // ---- trials_registry ---- trial_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit).
        modelBuilder.Entity<TrialsRegistryRow>(e =>
        {
            e.ToTable("trials_registry");
            e.HasKey(x => x.TrialId);
            e.Property(x => x.TrialId).HasColumnName("trial_id");
            e.Property(x => x.StrategyId).HasColumnName("strategy_id").IsRequired();
            e.Property(x => x.RegisteredOn).HasColumnName("registered_on").IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- power_reports ---- report_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit).
        modelBuilder.Entity<PowerReportRow>(e =>
        {
            e.ToTable("power_reports");
            e.HasKey(x => x.ReportId);
            e.Property(x => x.ReportId).HasColumnName("report_id");
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.StrategyA).HasColumnName("strategy_a").IsRequired();
            e.Property(x => x.StrategyB).HasColumnName("strategy_b").IsRequired();
            e.Property(x => x.TDays).HasColumnName("t_days").IsRequired();
            e.Property(x => x.SigmaLr).HasColumnName("sigma_lr").IsRequired();
            e.Property(x => x.NwLag).HasColumnName("nw_lag").IsRequired();
            e.Property(x => x.MdeAnn).HasColumnName("mde_ann").IsRequired();
            e.Property(x => x.ObservedGapAnn).HasColumnName("observed_gap_ann");
            e.Property(x => x.Verdict).HasColumnName("verdict");
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- go_live_log ---- event_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit).
        modelBuilder.Entity<GoLiveLogRow>(e =>
        {
            e.ToTable("go_live_log");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.Promoted).HasColumnName("promoted");
            e.Property(x => x.Demoted).HasColumnName("demoted");
            e.Property(x => x.Verdict).HasColumnName("verdict").IsRequired();
            e.Property(x => x.EvidenceJson).HasColumnName("evidence_json").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- allocation_log ---- event_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit).
        modelBuilder.Entity<AllocationLogRow>(e =>
        {
            e.ToTable("allocation_log");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.WeightsJson).HasColumnName("weights_json").IsRequired();
            e.Property(x => x.Reason).HasColumnName("reason").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
        });

        // ---- overfitting_checks ---- check_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit).
        // Covering index ix_overfitting_checks_path(strategy_id, signal, as_of): the FR-35/FR-39
        // reconstruction reads the signal='S3' path per strategy (SCHEMA:287-290). No CHECK on signal.
        modelBuilder.Entity<OverfittingCheckRow>(e =>
        {
            e.ToTable("overfitting_checks");
            e.HasKey(x => x.CheckId);
            e.Property(x => x.CheckId).HasColumnName("check_id");
            e.Property(x => x.StrategyId).HasColumnName("strategy_id").IsRequired();
            e.Property(x => x.AsOf).HasColumnName("as_of").IsRequired();
            e.Property(x => x.Signal).HasColumnName("signal").IsRequired();
            e.Property(x => x.Value).HasColumnName("value");
            e.Property(x => x.ThresholdJson).HasColumnName("threshold_json").IsRequired();
            e.Property(x => x.Contribution).HasColumnName("contribution").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").IsRequired().HasDefaultValue("live");
            e.HasIndex(x => new { x.StrategyId, x.Signal, x.AsOf }).HasDatabaseName("ix_overfitting_checks_path");
        });

        // ---- overfitting_status ---- PK (strategy_id, as_of, run_kind); status CHECK.
        modelBuilder.Entity<OverfittingStatusRow>(e =>
        {
            e.ToTable("overfitting_status", t => t.HasCheckConstraint(
                "ck_overfitting_status_status", "status IN ('healthy','warning','suspect','retired')"));
            e.HasKey(x => new { x.StrategyId, x.AsOf, x.RunKind });
            e.Property(x => x.StrategyId).HasColumnName("strategy_id");
            e.Property(x => x.AsOf).HasColumnName("as_of");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.TriggerJson).HasColumnName("trigger_json").IsRequired();
            e.Property(x => x.RunKind).HasColumnName("run_kind").HasDefaultValue("live");
        });

        // ---- journal_entries (D52) ---- entry_id bare INTEGER PK (NO AUTOINCREMENT — hand-edit);
        // kind + outcome CHECKs. REFERENCES links are documentary (no EF FK).
        modelBuilder.Entity<JournalEntryRow>(e =>
        {
            e.ToTable("journal_entries", t =>
            {
                t.HasCheckConstraint("ck_journal_entries_kind",
                    "kind IN ('hypothesis','observation','decision_note','skeptic_review','outcome')");
                t.HasCheckConstraint("ck_journal_entries_outcome",
                    "outcome IN ('confirmed','refuted','inconclusive')");
            });
            e.HasKey(x => x.EntryId);
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.CreatedOn).HasColumnName("created_on").IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.Title).HasColumnName("title").IsRequired();
            e.Property(x => x.BodyMd).HasColumnName("body_md").IsRequired();
            e.Property(x => x.StrategyId).HasColumnName("strategy_id");
            e.Property(x => x.LinkedEntryId).HasColumnName("linked_entry_id");
            e.Property(x => x.Metric).HasColumnName("metric");
            e.Property(x => x.EvidenceWindowDays).HasColumnName("evidence_window_days");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.Locked).HasColumnName("locked").IsRequired().HasDefaultValue(false);
            // D89 (v1.9.35) / M5: the FR-40 gate's pre-declared expected annualized effect. REAL, nullable.
            e.Property(x => x.ExpectedEffectAnn).HasColumnName("expected_effect_ann");
        });

        // ---- replay_regime_outcomes (D89/FR-41; M5) ---- PK (strategy_id, regime_episode_id, run_kind);
        // composite, so no autoincrement question. run_kind DEFAULT 'replay' (replay-only by construction,
        // D37). regime_episode_id REFERENCES regime_episodes — documentary, no EF FK (house precedent).
        modelBuilder.Entity<ReplayRegimeOutcomeRow>(e =>
        {
            e.ToTable("replay_regime_outcomes");
            e.HasKey(x => new { x.StrategyId, x.RegimeEpisodeId, x.RunKind });
            e.Property(x => x.StrategyId).HasColumnName("strategy_id");
            e.Property(x => x.RegimeEpisodeId).HasColumnName("regime_episode_id");
            e.Property(x => x.RunKind).HasColumnName("run_kind").HasDefaultValue("replay");
            e.Property(x => x.EdgeAnn).HasColumnName("edge_ann");
            e.Property(x => x.MedianPercentile).HasColumnName("median_percentile");
            e.Property(x => x.NDays).HasColumnName("n_days").IsRequired();
        });
    }
}
