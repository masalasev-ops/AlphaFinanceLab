using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data;

/// <summary>
/// The single EF Core context over the arena's SQLite file. Phase 0 maps only the five
/// infrastructure tables (runs, catchup_log, config, worker_state, jobs). All names are
/// snake_case to match SCHEMA_v1.9 exactly.
/// </summary>
public sealed class AlphaLabDbContext(DbContextOptions<AlphaLabDbContext> options) : DbContext(options)
{
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<CatchupLogRow> CatchupLog => Set<CatchupLogRow>();
    public DbSet<ConfigRow> Config => Set<ConfigRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<WorkerStateRow> WorkerState => Set<WorkerStateRow>();

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
    }
}
