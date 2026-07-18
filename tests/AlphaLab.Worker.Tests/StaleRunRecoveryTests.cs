using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Worker.Tests.Pipeline;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-CrashedRun + the launch-time FR34_NoOverlappingWriters guard (checkpoint 2.12 / D72). Step 1 of the
/// launch order: an orphaned run_in_progress (a prior crash) is cleared before catch-up; a FRESH heartbeat
/// (a live writer) fails the launch closed instead.
/// </summary>
public class StaleRunRecoveryTests
{
    // ---- FX-CrashedRun: run_in_progress=1 + stale heartbeat + a 'running' row ⇒ recovered ----
    [Fact]
    public async Task StaleRun_IsRecovered_RunMarkedFailedAndFlagCleared()
    {
        using var h = new PipelineHarness();
        long runId;
        using (var db = h.Open())
        {
            var run = new RunRow
            {
                AsOf = h.Run1, RunKind = "live", Watermark = $"{h.Run1}T22:00:00Z",
                StartedAt = Iso(h.Now.AddMinutes(-30)), Status = "running",
            };
            db.Runs.Add(run);
            db.SaveChanges();
            runId = run.RunId;

            var state = db.WorkerState.Find(1)!;
            state.RunInProgress = 1;
            state.CurrentRunId = runId;
            state.HeartbeatAt = Iso(h.Now.AddMinutes(-30)); // well past the 300s stale threshold
            db.SaveChanges();
        }

        var result = await h.RunStaleRecoveryAsync();

        Assert.True(result.RecoveredOrphan);
        Assert.Equal(runId, result.OrphanedRunId);
        using var check = h.Open();
        var recovered = check.Runs.Single(r => r.RunId == runId);
        Assert.Equal("failed", recovered.Status);
        Assert.NotNull(recovered.FinishedAt);
        var st = check.WorkerState.Find(1)!;
        Assert.Equal(0, st.RunInProgress);
        Assert.Null(st.CurrentRunId);
    }

    // ---- FR34_NoOverlappingWriters (partial): a FRESH heartbeat ⇒ a live writer ⇒ fail closed ----
    [Fact]
    public async Task FreshHeartbeat_AnotherWriterLive_FailsClosed()
    {
        using var h = new PipelineHarness();
        using (var db = h.Open())
        {
            var state = db.WorkerState.Find(1)!;
            state.RunInProgress = 1;
            state.CurrentRunId = 7;
            state.HeartbeatAt = Iso(h.Now.AddSeconds(-10)); // fresh (< 300s) ⇒ someone is writing
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<OverlappingWriterException>(() => h.RunStaleRecoveryAsync());

        // The flag is untouched — we must not clear another writer's in-progress marker.
        using var check = h.Open();
        Assert.Equal(1, check.WorkerState.Find(1)!.RunInProgress);
    }

    // ---- clean launch: run_in_progress=0 ⇒ nothing to recover ----
    [Fact]
    public async Task CleanLaunch_NoRunInProgress_IsANoop()
    {
        using var h = new PipelineHarness();

        var result = await h.RunStaleRecoveryAsync();

        Assert.False(result.RecoveredOrphan);
        Assert.Null(result.OrphanedRunId);
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
