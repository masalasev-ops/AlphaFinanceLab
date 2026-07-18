using System.Globalization;

namespace AlphaLab.Data.Services;

/// <summary>
/// The worker-liveness verdict read from worker_state (D72). Two independent facts drive every caller's
/// decision, so both are surfaced rather than collapsed into one boolean:
///   <see cref="IsLive"/>  — a writer is ACTIVELY running (run_in_progress=1 AND a fresh heartbeat). This
///                           is the API's 409 decision (a command must not race the daily write, rule 19).
///   <see cref="IsStale"/> — run_in_progress=1 but the heartbeat is stale (or absent): an ORPHANED crash.
///                           The next Worker launch recovers it (marks the run 'failed', clears the flag) —
///                           it must NEVER 409 the command path (D72/rule 24).
/// A clean idle store is neither (run_in_progress=0).
/// </summary>
public readonly record struct WorkerLiveness(bool RunInProgress, bool HeartbeatFresh)
{
    /// <summary>A writer is actively running — the daily transaction may be open right now.</summary>
    public bool IsLive => RunInProgress && HeartbeatFresh;

    /// <summary>run_in_progress is set but nobody is heartbeating — an orphaned run awaiting recovery.</summary>
    public bool IsStale => RunInProgress && !HeartbeatFresh;
}

/// <summary>
/// Pure liveness arithmetic (D72). Splitting the decision from any I/O keeps it framework-agnostic and
/// unit-testable, and lets the same verdict drive BOTH the Worker's stale-run recovery (checkpoint 2.12)
/// and the API's 409 gate (FR-32, Phase 3) without either owning timezone or SQLite concerns.
/// </summary>
public static class WorkerLivenessEvaluator
{
    /// <summary>
    /// A heartbeat is FRESH iff it parses as a UTC timestamp AND now − heartbeat ≤ threshold. A missing or
    /// unparseable heartbeat is NOT fresh (fail closed toward "orphaned"): treating an unknown as live
    /// would let a dead run 409 commands forever, which is exactly what D72 forbids.
    /// </summary>
    public static WorkerLiveness Evaluate(int runInProgress, string? heartbeatAt, DateTimeOffset now, int thresholdSeconds)
    {
        var inProgress = runInProgress != 0;
        if (!inProgress) return new WorkerLiveness(false, false);

        var fresh = TryParseUtc(heartbeatAt, out var beat)
            && (now - beat) <= TimeSpan.FromSeconds(Math.Max(0, thresholdSeconds));
        return new WorkerLiveness(true, fresh);
    }

    private static bool TryParseUtc(string? iso, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(iso)) return false;
        return DateTimeOffset.TryParse(
            iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}

/// <summary>Reads the single worker_state row and returns the D72 liveness verdict. Lives in AlphaLab.Data so
/// the API reaches it via Api→Data (Phase 3); the staleness threshold is supplied by the caller (the Worker
/// passes Worker.StaleRunThresholdSeconds; the API binds its own) so Data stays ignorant of Worker options.</summary>
public interface IWorkerLiveness
{
    Task<WorkerLiveness> GetAsync(int stalenessThresholdSeconds, CancellationToken ct = default);
}

/// <summary>EF-backed <see cref="IWorkerLiveness"/> over worker_state (single row, id=1). Read-only.</summary>
public sealed class WorkerLivenessReader(AlphaLabDbContext db, TimeProvider clock) : IWorkerLiveness
{
    public async Task<WorkerLiveness> GetAsync(int stalenessThresholdSeconds, CancellationToken ct = default)
    {
        var state = await db.WorkerState.FindAsync([1], ct).ConfigureAwait(false);
        if (state is null) return new WorkerLiveness(false, false); // no row ⇒ idle (nothing to gate on)
        return WorkerLivenessEvaluator.Evaluate(
            state.RunInProgress, state.HeartbeatAt, clock.GetUtcNow(), stalenessThresholdSeconds);
    }
}
