using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-JobDrain (checkpoint 2.12 / D72): step 3 of the launch order drains queued jobs OUTSIDE any write
/// transaction. A registered executor takes a job queued→running→done, and — to prove the drainer holds no
/// open transaction — the fake executor opens its OWN write transaction (which SQLite's single writer would
/// block if one were held). Phase 2 registers no executor, so an unregistered kind fails closed with a named
/// reason rather than sitting 'queued' forever.
/// </summary>
public class JobDrainTests
{
    [Fact]
    public async Task Drain_WithRegisteredExecutor_RunsJobToDone_OutsideAnyTransaction()
    {
        using var h = new PipelineHarness(configure: s => s.AddSingleton<IJobExecutor, FakeReplayExecutor>());
        long jobId;
        using (var db = h.Open())
        {
            var job = new JobRow { Kind = "replay", Status = "queued", SubmittedAt = "2026-07-17T10:00:00Z", RequestJson = "{}" };
            db.Jobs.Add(job);
            db.SaveChanges();
            jobId = job.JobId;
        }

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(1, outcome.Queued);
        Assert.Equal(1, outcome.Done);
        Assert.Equal(0, outcome.Failed);

        using var check = h.Open();
        var done = check.Jobs.Single(j => j.JobId == jobId);
        Assert.Equal("done", done.Status);
        Assert.NotNull(done.StartedAt);
        Assert.NotNull(done.FinishedAt);
        // The executor's OWN-transaction write landed ⇒ no writer lock was held by the drainer.
        Assert.Equal(ExecutedMarker, check.WorkerState.Find(1)!.HeartbeatAt);
    }

    [Fact]
    public async Task Drain_UnregisteredKind_FailsClosedWithNamedReason()
    {
        using var h = new PipelineHarness(); // Phase-2 default: NO executors registered
        long jobId;
        using (var db = h.Open())
        {
            var job = new JobRow { Kind = "replay", Status = "queued", SubmittedAt = "2026-07-17T10:00:00Z", RequestJson = "{}" };
            db.Jobs.Add(job);
            db.SaveChanges();
            jobId = job.JobId;
        }

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(1, outcome.Failed);
        Assert.Equal(0, outcome.Done);
        using var check = h.Open();
        var failed = check.Jobs.Single(j => j.JobId == jobId);
        Assert.Equal("failed", failed.Status);            // never left 'queued'
        Assert.NotNull(failed.ErrorJson);
        Assert.Contains("no executor registered", failed.ErrorJson!);
        Assert.Contains("replay", failed.ErrorJson!);
    }

    [Fact]
    public async Task Drain_EmptyQueue_IsANoop()
    {
        using var h = new PipelineHarness();

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(0, outcome.Queued);
        Assert.Equal(0, outcome.Done);
        Assert.Equal(0, outcome.Failed);
    }

    private const string ExecutedMarker = "JOB-EXECUTED";

    /// <summary>A test-only executor for kind 'replay'. Opens its own write transaction (proving the drainer
    /// holds none) and stamps a marker so the test can confirm it actually ran.</summary>
    private sealed class FakeReplayExecutor(IServiceScopeFactory scopeFactory) : IJobExecutor
    {
        public string Kind => "replay";

        public Task ExecuteAsync(JobRow job, CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AlphaLabDbContext>();
            using var txn = db.Database.BeginTransaction();
            db.WorkerState.Find(1)!.HeartbeatAt = ExecutedMarker;
            db.SaveChanges();
            txn.Commit();
            return Task.CompletedTask;
        }
    }
}
