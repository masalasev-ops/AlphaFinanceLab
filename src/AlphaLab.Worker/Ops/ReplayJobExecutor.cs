using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// The jobs.kind='replay' executor (FR-19/FR-32, D57/D60): the API's 202+job_id command path lands
/// here — the JobDrainer runs it AFTER catch-up, outside any daily write transaction (D72). The
/// request_json is a <see cref="ReplayRequest"/>; execution delegates to the same
/// <see cref="ReplayRunner"/> the `replay-calibrate` verb drives, so the two entry points cannot
/// drift. A malformed request throws ⇒ the drainer marks the job 'failed' with the reason (rule 10).
/// </summary>
public sealed class ReplayJobExecutor(
    IConfiguration configuration,
    ArenaOptions arena,
    string connectionString,
    ILoggerFactory loggerFactory) : IJobExecutor
{
    public string Kind => "replay";

    public async Task ExecuteAsync(JobRow job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var request = JsonSerializer.Deserialize<ReplayRequest>(job.RequestJson)
            ?? throw new InvalidOperationException(
                $"jobs.request_json for job {job.JobId} does not deserialize to a ReplayRequest (fail closed).");

        var outcome = await new ReplayRunner(configuration, arena, loggerFactory)
            .RunAsync(connectionString, request, ct).ConfigureAwait(false);

        if (outcome.StoppedEarly)
        {
            // A stopped replay is a FAILED job (the drainer records the message); the committed prefix
            // persists and a re-enqueued job resumes it.
            throw new InvalidOperationException(
                $"Replay stopped early at {outcome.SessionsCommitted}/{outcome.SessionsPlanned} session(s): {outcome.StopReason}");
        }
    }
}
