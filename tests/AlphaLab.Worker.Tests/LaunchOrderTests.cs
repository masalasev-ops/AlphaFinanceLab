using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Worker.Tests.Pipeline;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FR34_LabRunsWithoutApi (checkpoint 2.12 / D72): the whole OnDemand launch order runs end-to-end with only
/// AlphaLab.Worker + AlphaLab.Data present — no Api, no scheduler. The harness never constructs an API, so a
/// completing launch IS the proof. Also verifies the ORDER matters: stale-run recovery must precede catch-up
/// (an orphan is cleared, then its session is replayed fresh).
/// </summary>
public class LaunchOrderTests
{
    // Default harness clock ⇒ last completed session = Run3 ⇒ the natural gap is Run1..Run3.
    [Fact]
    public async Task FullLaunchOrder_CleanStore_CatchesUpDrainsAndBacksUp()
    {
        using var h = new PipelineHarness();

        var (recovery, catchup, drain, backup) = await h.RunLaunchAsync();

        Assert.False(recovery.RecoveredOrphan);          // 1. nothing to recover on a clean store
        Assert.Equal(3, catchup.Processed);               // 2. the gap replayed
        Assert.False(catchup.StoppedEarly);
        Assert.NotNull(drain);                            // 3. drain ran (DrainQueuedJobsOnLaunch default true)
        Assert.Equal(0, drain!.Queued);                   //    …nothing queued
        Assert.True(backup.Created);                      // 4. a dated backup landed
        Assert.True(File.Exists(backup.BackupPath));

        using var db = h.Open();
        Assert.Equal(3, db.Runs.Count(r => r.Status == "ok"));
        Assert.Equal(0, db.WorkerState.Find(1)!.RunInProgress); // clean at exit
    }

    [Fact]
    public async Task FullLaunchOrder_AfterCrash_RecoversThenCatchesUp()
    {
        using var h = new PipelineHarness();
        // Simulate a crash mid-Run1: a 'running' row + run_in_progress set with a stale heartbeat (its bars
        // rolled back, so none exist). Recovery must clear this BEFORE catch-up replays the session.
        using (var db = h.Open())
        {
            var orphan = new RunRow
            {
                AsOf = h.Run1, RunKind = "live", Watermark = $"{h.Run1}T22:00:00Z",
                StartedAt = Iso(h.Now.AddMinutes(-30)), Status = "running",
            };
            db.Runs.Add(orphan);
            db.SaveChanges();
            var st = db.WorkerState.Find(1)!;
            st.RunInProgress = 1;
            st.CurrentRunId = orphan.RunId;
            st.HeartbeatAt = Iso(h.Now.AddMinutes(-30));
            db.SaveChanges();
        }

        var (recovery, catchup, _, backup) = await h.RunLaunchAsync();

        Assert.True(recovery.RecoveredOrphan);            // the orphan was cleared first
        Assert.Equal(3, catchup.Processed);               // …then the gap (incl. Run1) replayed fresh
        Assert.True(backup.Created);

        using var check = h.Open();
        // Run1 now has exactly one 'ok' run (the replay) and the orphan marked 'failed'.
        Assert.Single(check.Runs.Where(r => r.AsOf == h.Run1 && r.Status == "ok").ToList());
        Assert.Contains(check.Runs.ToList(), r => r.AsOf == h.Run1 && r.Status == "failed");
        Assert.Equal(0, check.WorkerState.Find(1)!.RunInProgress);
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
