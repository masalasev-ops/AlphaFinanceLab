namespace AlphaLab.Data.Entities;

// The five Phase-0 infrastructure tables (SCHEMA_v1.9 §; D59/D60). No data-domain tables —
// those arrive in Phase 1. Timestamps are TEXT (UTC ISO-8601) per SCHEMA. Column/table names
// are mapped to snake_case in AlphaLabDbContext.OnModelCreating.

/// <summary>runs — one row per processed trading day. status is defaulted-but-UNCONSTRAINED (no CHECK).</summary>
public sealed class RunRow
{
    public long RunId { get; set; }
    public string AsOf { get; set; } = default!;
    /// <summary>CHECK IN ('live','catchup','replay').</summary>
    public string RunKind { get; set; } = default!;
    public string Watermark { get; set; } = default!;
    public string StartedAt { get; set; } = default!;
    public string? FinishedAt { get; set; }
    /// <summary>running | ok | failed — DEFAULT 'running'. Deliberately NOT constrained by a CHECK (SCHEMA).</summary>
    public string Status { get; set; } = "running";
    public string? InputsHash { get; set; }
}

/// <summary>catchup_log — recovered sessions (D47). Created-but-dormant in Phase 0.</summary>
public sealed class CatchupLogRow
{
    public string AsOf { get; set; } = default!;
    public string RecoveredAt { get; set; } = default!;
    public long RunId { get; set; }
}

/// <summary>
/// config — append-only versioned settings. Composite PK (key, version) (finding 108).
/// Current value of a key = row with MAX(version); a change INSERTs (key, version+1), never
/// UPDATE/DELETE. Version is writer-supplied (ValueGeneratedNever), computed as MAX(version)+1
/// inside one transaction at write time (D56). Created-but-dormant in Phase 0.
/// </summary>
public sealed class ConfigRow
{
    public string Key { get; set; } = default!;
    public string ValueJson { get; set; } = default!;
    public int Version { get; set; }
    public string ChangedOn { get; set; } = default!;
    public string? Reason { get; set; }
}

/// <summary>jobs — async command queue (API enqueues, Worker executes). Created-but-dormant in Phase 0.</summary>
public sealed class JobRow
{
    public long JobId { get; set; }
    /// <summary>CHECK IN ('replay','analysis_brief','analysis_skeptic').</summary>
    public string Kind { get; set; } = default!;
    /// <summary>CHECK IN ('queued','running','done','failed') — DEFAULT 'queued'.</summary>
    public string Status { get; set; } = "queued";
    public string SubmittedAt { get; set; } = default!;
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public string RequestJson { get; set; } = default!;
    public string? ResultRef { get; set; }
    public string? ErrorJson { get; set; }
}

/// <summary>
/// worker_state — a single row (id CHECK = 1), seeded by InitialCreate. The API's 409/queue
/// decision reads run_in_progress; the Worker heartbeats heartbeat_at (D72).
/// </summary>
public sealed class WorkerStateRow
{
    /// <summary>PK, CHECK (id = 1). Writer-supplied (ValueGeneratedNever).</summary>
    public int Id { get; set; }
    /// <summary>0/1. INTEGER NOT NULL DEFAULT 0.</summary>
    public int RunInProgress { get; set; }
    public long? CurrentRunId { get; set; }
    /// <summary>D72: written by the running Worker at least every Worker.HeartbeatSeconds.</summary>
    public string? HeartbeatAt { get; set; }
}
