using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>
/// The OnDemand launch runner (D61/D72). A BackgroundService, so its ExecuteAsync runs AFTER every
/// hosted-service StartAsync — the schema is verified present (SchemaStartup ran first) and the
/// HeartbeatService is already ticking alongside.
///
/// THE D72 LAUNCH ORDER (checkpoint 2.12), in exactly this sequence:
///   1. StaleRunRecovery — clear an orphaned run_in_progress from a prior crash (mark its run 'failed'),
///      so the API is never 409'd forever and the crashed day becomes a normal missed session. A FRESH
///      heartbeat instead means a live writer: it throws (fail closed, sole-writer rule D59) and the launch
///      aborts non-zero.
///   2. CatchupRunner — replay every missed session in order, one transaction per day (D47).
///   3. JobDrainer — drain queued async commands OUTSIDE any write transaction (behind
///      Worker.DrainQueuedJobsOnLaunch). Phase 2 registers no executor, so a queued job fails closed.
///   4. LocalBackup — checkpoint + dated copy + prune (RUNBOOK §3).
///   5. exit (StopApplication).
///
/// A crash inside a day rolls that day back (its transaction) but leaves the committed prefix; the exception
/// propagates so the host logs it + exits non-zero, and the next launch's step 1 recovers the orphan.
/// </summary>
public sealed class OnDemandRunner(
    StaleRunRecovery staleRecovery,
    CatchupRunner catchup,
    JobDrainer jobDrainer,
    LocalBackup backup,
    WorkerOptions options,
    IHostApplicationLifetime lifetime,
    ArenaOptions arena,
    ILogger<OnDemandRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var arenaScope = logger.BeginArenaScope(arena);

        // 1. Recover a stale run (or fail closed if another writer is live).
        await staleRecovery.RecoverAsync(stoppingToken).ConfigureAwait(false);

        // 2. Catch up through the last completed session.
        await catchup.RunAsync(stoppingToken).ConfigureAwait(false);

        // 3. Drain queued jobs (outside any write transaction — catch-up has returned).
        if (options.DrainQueuedJobsOnLaunch)
        {
            await jobDrainer.DrainAsync(stoppingToken).ConfigureAwait(false);
        }
        else
        {
            logger.LogInformation("Job drain skipped (Worker.DrainQueuedJobsOnLaunch=false).");
        }

        // 4. Per-launch local backup.
        await backup.BackupAsync(stoppingToken).ConfigureAwait(false);

        // 5. Exit.
        lifetime.StopApplication();
    }
}
