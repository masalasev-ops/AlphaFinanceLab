using AlphaLab.Data;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// End-to-end WAL verification (checkpoint 3.5.2, FR-25). SchemaStartup already proves the store
/// REPORTS journal_mode=wal (R1_SchemaStartup_EnablesWal, finding 118). What was never proved is that
/// a checkpoint on it COMPLETES — the assumption LocalBackup rests on when it folds the WAL into the
/// main file so a plain copy is a consistent snapshot.
/// </summary>
public class WalVerificationTests
{
    [Fact]
    public void FR25_VerifyWal_OnAWalStore_Passes()
    {
        using var h = new PipelineHarness();   // the harness enables WAL on its arena, as the Worker does
        using var db = h.Open();

        var result = WalVerification.Verify(db);

        Assert.True(result.Ok, result.FailureReason);
        Assert.Equal("wal", result.JournalMode, ignoreCase: true);
        Assert.True(result.CheckpointCompleted);
    }

    [Fact]
    public void FR25_VerifyWal_OnARollbackJournalStore_FailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alphalab-nowal-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            using (var db = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(cs).Options))
            {
                db.Database.Migrate();
                // Explicitly a rollback-journal store: the defect verify-wal exists to catch. Nothing at
                // runtime would announce it — the lab keeps working, minus the reader-during-write
                // concurrency the Api depends on and the checkpoint the backup assumes.
                db.Database.ExecuteSqlRaw("PRAGMA journal_mode=DELETE;");
            }

            using (var db = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(cs).Options))
            {
                var result = WalVerification.Verify(db);

                Assert.False(result.Ok);
                Assert.False(result.CheckpointCompleted);
                Assert.Equal("delete", result.JournalMode, ignoreCase: true);
                Assert.Contains("not 'wal'", result.FailureReason!, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + s)) File.Delete(path + s);
            }
        }
    }

    [Fact]
    public void FR25_VerifyWal_NeverSetsTheMode()
    {
        // A verifier that repaired what it checks could never report the defect it exists to find.
        var path = Path.Combine(Path.GetTempPath(), $"alphalab-nowal2-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            using (var db = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(cs).Options))
            {
                db.Database.Migrate();
                db.Database.ExecuteSqlRaw("PRAGMA journal_mode=DELETE;");
            }

            using (var db = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(cs).Options))
            {
                WalVerification.Verify(db);
            }

            // Still DELETE after verification — the store was inspected, not converted.
            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("delete", cmd.ExecuteScalar()?.ToString(), ignoreCase: true);
            }
        }
        finally
        {
            foreach (var s in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + s)) File.Delete(path + s);
            }
        }
    }
}
