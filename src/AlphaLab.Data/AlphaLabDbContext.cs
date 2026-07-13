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
        });

        // ---- corporate_actions ---- action_id bare INTEGER PK (NO AUTOINCREMENT); 8-value type CHECK.
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
            // decimal → TEXT (D69). EF's default SQLite decimal mapping is TEXT; declared explicitly.
            e.Property(x => x.CashPerShare).HasColumnName("cash_per_share").HasColumnType("TEXT");
            e.Property(x => x.Ratio).HasColumnName("ratio");
            e.Property(x => x.CounterpartySecurityId).HasColumnName("counterparty_security_id");
            e.Property(x => x.NewSymbol).HasColumnName("new_symbol");
            e.Property(x => x.ObservedAt).HasColumnName("observed_at").IsRequired();
            e.Property(x => x.Source).HasColumnName("source").IsRequired().HasDefaultValue("eodhd");
            e.Property(x => x.ProcessedOn).HasColumnName("processed_on");
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
    }
}
