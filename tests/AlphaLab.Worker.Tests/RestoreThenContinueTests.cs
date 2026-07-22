using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Data.Sqlite;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-RestoreThenContinue (TEST_PLAN §3 / FR-25, checkpoint 3.5.4) — the RUNBOOK §4 restore drill as
/// an executable test.
///
/// RUNBOOK §4 says: stop the app, copy the target backup over alphalab.db, start, and the app detects
/// the DB is behind and catches up automatically. That procedure has been documented since v1.9 and
/// never once exercised — and, as §4 itself puts it, "an unrehearsed backup is a hope, not a plan."
/// The drill IS the test that backups work: it is the only thing that proves the per-launch
/// LocalBackup copy is actually restorable AND that the D47 catch-up picks up from a restored,
/// behind-the-clock store rather than sitting idle or double-counting.
///
/// The sequence mirrors the runbook exactly, including deleting the -wal/-shm sidecars: the backup is
/// taken after wal_checkpoint(TRUNCATE), so it is complete on its own, and leaving a NEWER WAL beside
/// a restored older main file would silently replay the very transactions the restore was meant to
/// discard.
/// </summary>
public class RestoreThenContinueTests
{
    [Fact]
    public async Task FR25_RestoreThenContinue_CatchupResumesWithoutDataLoss()
    {
        using var h = new PipelineHarness();

        // Two sessions committed, then the operator's per-launch backup (D72 step 4).
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        var backup = await h.RunBackupAsync();
        Assert.True(backup.Created);

        // A third session commits AFTER the backup — this is what the restore will discard, and what
        // catch-up must then rebuild.
        await h.RunAsync(h.Run3);

        var beforeRestore = ReadState(h);
        Assert.Equal(3, beforeRestore.RunCount);
        Assert.Contains(h.Run3, beforeRestore.EquityDates);

        // ---- the drill (RUNBOOK §4 steps 1-3) ----
        RestoreOver(h.DbPath, backup.BackupPath!);

        var afterRestore = ReadState(h);
        Assert.Equal(2, afterRestore.RunCount);                    // Run3 is genuinely gone
        Assert.DoesNotContain(h.Run3, afterRestore.EquityDates);
        Assert.Contains(h.Run1, afterRestore.EquityDates);          // ...and the earlier days survived
        Assert.Contains(h.Run2, afterRestore.EquityDates);

        // ---- start: the lab detects it is behind and catches up (RUNBOOK §4 step 3) ----
        var missed = await h.ResolveMissedAsync();
        Assert.Contains(h.Run3, missed.Select(d => d.ToString("yyyy-MM-dd")));

        var catchup = await h.RunCatchupAsync();
        Assert.Equal(1, catchup.Processed);
        Assert.False(catchup.StoppedEarly);

        // ---- verify (RUNBOOK §4 step 4) ----
        var recovered = ReadState(h);
        Assert.Equal(3, recovered.RunCount);
        Assert.Equal(beforeRestore.EquityDates, recovered.EquityDates);   // curve continuous, no gap
        Assert.All(recovered.RunStatuses, s => Assert.Equal("ok", s));

        using var db = h.Open();
        // The recovered session is labelled 'live', not 'catchup' — correctly: D47 keys run_kind off
        // whether the session is being processed on its OWN ET date, and this drill restores and
        // recovers within the same ET day. (The catchup_log path for genuinely-late sessions is
        // covered by CatchupRunnerTests / FX-Outage5d, not here.)
        Assert.Equal("live", db.Runs.Single(r => r.AsOf == h.Run3).RunKind);
        // The D90 book is rebuilt for the recovered session, so the restored store is reproducible again.
        Assert.NotEmpty(db.PositionSnapshots.Where(p => p.AsOf == h.Run3).ToList());
    }

    [Fact]
    public async Task FR25_RestoreThenContinue_IsIdempotent_ASecondLaunchDoesNothing()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        var backup = await h.RunBackupAsync();
        await h.RunAsync(h.Run3);

        RestoreOver(h.DbPath, backup.BackupPath!);
        await h.RunCatchupAsync();

        // The restore + catch-up must land on the same place a normal run would, not somewhere that
        // needs a second correction pass (FR-7 idempotency, now across a restore boundary).
        var second = await h.RunCatchupAsync();

        Assert.Equal(0, second.MissedCount);
        Assert.Equal(0, second.Processed);
        Assert.Equal(3, ReadState(h).RunCount);
    }

    /// <summary>RUNBOOK §4 steps 1-2: stop, then copy the backup over the store. The -wal/-shm sidecars
    /// go with it — the backup is a post-checkpoint copy, so a surviving newer WAL would replay exactly
    /// the transactions the restore is discarding.</summary>
    private static void RestoreOver(string dbPath, string backupPath)
    {
        SqliteConnection.ClearAllPools();      // "stop the app" — release the store's file handles
        File.Copy(backupPath, dbPath, overwrite: true);
        foreach (var sidecar in new[] { dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    private static (int RunCount, List<string> EquityDates, List<string> RunStatuses) ReadState(PipelineHarness h)
    {
        using var db = h.Open();
        return (
            db.Runs.Count(),
            db.EquityCurve.Select(e => e.AsOf).Distinct().OrderBy(d => d).ToList(),
            db.Runs.Select(r => r.Status).ToList());
    }
}
