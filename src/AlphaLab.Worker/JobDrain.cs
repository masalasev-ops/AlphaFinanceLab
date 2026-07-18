using System.Globalization;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>
/// Executes one kind of queued job (the async command queue, D57/D60: the API enqueues, the Worker runs).
/// A concrete executor is registered per <see cref="Kind"/>; Phase 2 registers NONE — every long-running
/// command (replay, analysis briefs/skeptic) belongs to a later phase, so a queued job of any kind fails
/// closed with a named reason (never stuck at 'queued' forever). The first real executor arrives with FR-32
/// (Phase 3+).
/// </summary>
public interface IJobExecutor
{
    /// <summary>The jobs.kind this executor handles (e.g. "replay"). Must match the CHECK-constrained set.</summary>
    string Kind { get; }

    /// <summary>Run the job. A throw ⇒ the drainer marks the row 'failed' with the exception message.</summary>
    Task ExecuteAsync(JobRow job, CancellationToken ct);
}

/// <summary>What one drain pass did.</summary>
public sealed record JobDrainOutcome(int Queued, int Done, int Failed);

/// <summary>
/// Step 3 of the D72 launch order (checkpoint 2.12): drain queued jobs AFTER catch-up returns — never inside
/// a daily write transaction. Behind Worker.DrainQueuedJobsOnLaunch.
///
/// The "no open transaction" invariant is enforced structurally, twice: this asserts
/// db.Database.CurrentTransaction is null on entry, and FX-JobDrain's fake executor itself opens a write
/// transaction (which would deadlock against SQLite's single writer if the drainer held one).
///
/// EMPTY REGISTRY (Phase 2): no IJobExecutor is registered, so a queued job's kind resolves to nothing and
/// the row is marked 'failed' with error_json naming the missing executor — fail closed and visible (rule 10),
/// the exact opposite of silently leaving it 'queued'.
/// </summary>
public sealed class JobDrainer(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IJobExecutor> executors,
    TimeProvider clock,
    ArenaOptions arena,
    ILogger<JobDrainer> logger)
{
    // Kind -> executor. A duplicate kind is a wiring bug (two executors claim the same queue) — fail loudly.
    private readonly IReadOnlyDictionary<string, IJobExecutor> _byKind =
        executors.ToDictionary(e => e.Kind, StringComparer.Ordinal);

    public async Task<JobDrainOutcome> DrainAsync(CancellationToken ct = default)
    {
        using var arenaScope = logger.BeginArenaScope(arena);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlphaLabDbContext>();

        // Structural guard: the daily transaction must be closed by now (catch-up returned). If a transaction
        // were open, a job executor opening its own would deadlock on SQLite's single writer.
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "JobDrainer entered with an open transaction — jobs must drain OUTSIDE the daily write transaction (D72).");
        }

        var queued = db.Jobs.Where(j => j.Status == "queued").OrderBy(j => j.JobId).ToList();
        if (queued.Count == 0)
        {
            logger.LogInformation("Job drain: no queued jobs.");
            return new JobDrainOutcome(0, 0, 0);
        }

        var done = 0;
        var failed = 0;
        foreach (var job in queued)
        {
            ct.ThrowIfCancellationRequested();

            if (!_byKind.TryGetValue(job.Kind, out var executor))
            {
                // Fail closed: a real queued job with no executor is marked 'failed' with a named reason.
                MarkFailed(db, job, $"no executor registered for kind '{job.Kind}' (Phase 2 registers none)");
                failed++;
                logger.LogError("Job {JobId} kind '{Kind}': no executor registered — marked 'failed'.", job.JobId, job.Kind);
                continue;
            }

            job.Status = "running";
            job.StartedAt = NowIso();
            db.SaveChanges();

            try
            {
                await executor.ExecuteAsync(job, ct).ConfigureAwait(false);
                job.Status = "done";
                job.FinishedAt = NowIso();
                db.SaveChanges();
                done++;
                logger.LogInformation("Job {JobId} kind '{Kind}': done.", job.JobId, job.Kind);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkFailed(db, job, ex.Message);
                failed++;
                logger.LogError(ex, "Job {JobId} kind '{Kind}': executor threw — marked 'failed'.", job.JobId, job.Kind);
            }
        }

        logger.LogInformation("Job drain complete: {Done} done, {Failed} failed of {Queued} queued.", done, failed, queued.Count);
        return new JobDrainOutcome(queued.Count, done, failed);
    }

    private void MarkFailed(AlphaLabDbContext db, JobRow job, string reason)
    {
        job.Status = "failed";
        job.StartedAt ??= NowIso();
        job.FinishedAt = NowIso();
        job.ErrorJson = $"{{\"error\":{System.Text.Json.JsonSerializer.Serialize(reason)}}}";
        db.SaveChanges();
    }

    private string NowIso() => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
