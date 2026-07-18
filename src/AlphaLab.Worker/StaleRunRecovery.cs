using System.Globalization;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>What stale-run recovery did on launch. <see cref="RecoveredOrphan"/> ⇒ a crashed run was found
/// (run_in_progress was set with no fresh heartbeat) and cleared: its run row marked 'failed', the flag reset.</summary>
public sealed record StaleRunRecoveryResult(bool RecoveredOrphan, long? OrphanedRunId);

/// <summary>Thrown when a launch finds run_in_progress=1 with a FRESH heartbeat — another Worker is actively
/// writing. The lab is the sole DB writer (D59); a second one must NOT proceed (fail closed, rule 10). The
/// operator resolves it (the other process finishes, or is confirmed dead and the flag hand-cleared). This
/// is the partial FR34_NoOverlappingWriters guard at launch (the endpoint-level 409 lands with FR-32).</summary>
public sealed class OverlappingWriterException(long? currentRunId, string? heartbeatAt)
    : Exception($"Another Worker appears to be writing (run_in_progress=1, run_id={currentRunId?.ToString(CultureInfo.InvariantCulture) ?? "?"}, " +
                $"fresh heartbeat_at={heartbeatAt ?? "?"}). The lab is the sole writer (D59) — refusing to start a second run.");

/// <summary>
/// Step 1 of the D72 launch order (checkpoint 2.12): clear a stale run_in_progress before catch-up runs.
///
/// A crash inside the daily Stage-2 transaction rolls the day back but leaves worker_state.run_in_progress=1
/// and the run row 'running' (DailyPipeline commits those in a small txn BEFORE Stage 2, precisely so a crash
/// stays visible). Left alone, that flag would make the API 409 every command forever (rule 24). This finds
/// the orphan — run_in_progress=1 with NO fresh heartbeat — marks its run 'failed', and clears the flag, so
/// the run becomes a normal missed session the catch-up resolver replays.
///
/// If the heartbeat IS fresh, a live writer holds the store: this fails closed (<see cref="OverlappingWriterException"/>)
/// rather than double-write. The staleness bound is Worker.StaleRunThresholdSeconds (5× the 30s heartbeat) —
/// the shared verdict lives in the pure <see cref="WorkerLivenessEvaluator"/> (AlphaLab.Data), reused by the API.
/// </summary>
public sealed class StaleRunRecovery(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    TimeProvider clock,
    ArenaOptions arena,
    ILogger<StaleRunRecovery> logger)
{
    public async Task<StaleRunRecoveryResult> RecoverAsync(CancellationToken ct = default)
    {
        using var arenaScope = logger.BeginArenaScope(arena);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlphaLabDbContext>();

        var state = await db.WorkerState.FindAsync([1], ct).ConfigureAwait(false);
        if (state is null || state.RunInProgress == 0)
        {
            logger.LogInformation("Stale-run recovery: no run in progress — clean launch.");
            return new StaleRunRecoveryResult(false, null);
        }

        var liveness = WorkerLivenessEvaluator.Evaluate(
            state.RunInProgress, state.HeartbeatAt, clock.GetUtcNow(), options.StaleRunThresholdSeconds);

        if (liveness.IsLive)
        {
            // Fresh heartbeat ⇒ another writer is alive. Do NOT touch the flag or the run row.
            logger.LogCritical(
                "Stale-run recovery: run_in_progress=1 with a FRESH heartbeat (run_id={RunId}, heartbeat_at={Beat}) — another writer is live. Aborting.",
                state.CurrentRunId, state.HeartbeatAt);
            throw new OverlappingWriterException(state.CurrentRunId, state.HeartbeatAt);
        }

        var orphanId = state.CurrentRunId;
        using (var txn = db.Database.BeginTransaction())
        {
            if (orphanId is { } id)
            {
                var run = db.Runs.FirstOrDefault(r => r.RunId == id);
                if (run is { Status: "running" })
                {
                    run.Status = "failed";
                    run.FinishedAt = NowIso();
                }
            }
            state.RunInProgress = 0;
            state.CurrentRunId = null;
            db.SaveChanges();
            txn.Commit();
        }

        logger.LogWarning(
            "Stale-run recovery: cleared an orphaned run (run_id={RunId}, last heartbeat={Beat}) — marked 'failed'; catch-up will replay that session.",
            orphanId, state.HeartbeatAt ?? "(never)");
        return new StaleRunRecoveryResult(true, orphanId);
    }

    private string NowIso() => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
