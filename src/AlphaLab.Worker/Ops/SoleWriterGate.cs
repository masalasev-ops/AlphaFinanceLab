using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// The launch-time sole-writer gate for the OPS-VERB path (D59). It mirrors <c>StaleRunRecovery</c>,
/// which guards only the hosted path — an ops verb dispatches before the Generic Host exists, so no
/// hosted guard has run, yet a WRITING verb still writes.
///
/// Extracted here because there is now more than one writing verb (`replay-calibrate` and the FR-45
/// signal backfill). Two copies of a fail-closed concurrency check is exactly the shape where one gets
/// fixed and the other does not.
///
/// A FRESH heartbeat under run_in_progress=1 means another Worker is actively writing this arena ⇒
/// refuse. A STALE flag is a crash orphan ⇒ mark its run failed and clear it, so the caller sees a
/// clean store rather than being blocked forever by a process that died (D72).
/// </summary>
public static class SoleWriterGate
{
    /// <param name="verb">Named in the log lines so an operator can see WHICH writer refused.</param>
    public static void Guard(AlphaLabDbContext db, WorkerOptions options, ILogger logger, string verb)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var state = db.WorkerState.FirstOrDefault(w => w.Id == 1);
        if (state is null || state.RunInProgress == 0) return;

        var liveness = WorkerLivenessEvaluator.Evaluate(
            state.RunInProgress, state.HeartbeatAt, TimeProvider.System.GetUtcNow(), options.StaleRunThresholdSeconds);
        if (liveness.IsLive)
        {
            logger.LogCritical(
                "{Verb}: run_in_progress=1 with a FRESH heartbeat (run_id={RunId}, heartbeat_at={Beat}) — " +
                "another writer is live. Refusing to start (sole writer, D59).",
                verb, state.CurrentRunId, state.HeartbeatAt);
            throw new OverlappingWriterException(state.CurrentRunId, state.HeartbeatAt);
        }

        var orphanId = state.CurrentRunId;
        if (orphanId is { } id)
        {
            var run = db.Runs.FirstOrDefault(r => r.RunId == id);
            if (run is { Status: "running" })
            {
                run.Status = "failed";
                run.FinishedAt = TimeProvider.System.GetUtcNow().UtcDateTime
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }
        }
        state.RunInProgress = 0;
        state.CurrentRunId = null;
        db.SaveChanges();
        logger.LogWarning("{Verb}: cleared a stale run_in_progress flag from a crashed writer (run_id={RunId}).",
            verb, orphanId);
    }
}
