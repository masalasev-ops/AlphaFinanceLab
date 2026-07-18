using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The D72 worker-liveness verdict (checkpoint 2.12) — the 409 decision the API reaches in Phase 3 and the
/// input to the Worker's stale-run recovery. The pure evaluator is tested directly; one reader test proves
/// it reads worker_state.
/// </summary>
public class WorkerLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Idle_RunInProgressZero_IsNeitherLiveNorStale()
    {
        var v = WorkerLivenessEvaluator.Evaluate(runInProgress: 0, heartbeatAt: Iso(Now), Now, thresholdSeconds: 300);
        Assert.False(v.IsLive);
        Assert.False(v.IsStale);
    }

    [Fact]
    public void FreshHeartbeat_IsLive_NotStale()
    {
        // Heartbeat 30s ago, threshold 300s ⇒ fresh ⇒ a writer is actively running (409 territory).
        var v = WorkerLivenessEvaluator.Evaluate(1, Iso(Now.AddSeconds(-30)), Now, 300);
        Assert.True(v.IsLive);
        Assert.False(v.IsStale);
    }

    [Fact]
    public void StaleHeartbeat_IsStale_NotLive()
    {
        // Heartbeat 10 minutes ago, threshold 300s ⇒ stale ⇒ an orphaned crash (recover, never 409).
        var v = WorkerLivenessEvaluator.Evaluate(1, Iso(Now.AddMinutes(-10)), Now, 300);
        Assert.True(v.IsStale);
        Assert.False(v.IsLive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-timestamp")]
    public void RunInProgressWithNoUsableHeartbeat_IsStale_FailClosed(string? heartbeat)
    {
        // Missing/unparseable heartbeat while in progress ⇒ treated as orphaned, never as live.
        var v = WorkerLivenessEvaluator.Evaluate(1, heartbeat, Now, 300);
        Assert.True(v.IsStale);
        Assert.False(v.IsLive);
    }

    [Fact]
    public void Boundary_HeartbeatExactlyAtThreshold_IsStillFresh()
    {
        var v = WorkerLivenessEvaluator.Evaluate(1, Iso(Now.AddSeconds(-300)), Now, 300);
        Assert.True(v.IsLive); // now - beat == threshold ⇒ <= ⇒ fresh
    }

    [Fact]
    public async Task Reader_ReadsWorkerStateRow()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using (var db = TestDb.Open(path))
            {
                var state = db.WorkerState.Find(1)!;
                state.RunInProgress = 1;
                state.CurrentRunId = 42;
                state.HeartbeatAt = Iso(Now.AddSeconds(-30));
                db.SaveChanges();
            }

            using var readDb = TestDb.Open(path);
            var reader = new WorkerLivenessReader(readDb, new FixedClock(Now));
            var v = await reader.GetAsync(stalenessThresholdSeconds: 300);

            Assert.True(v.IsLive);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
