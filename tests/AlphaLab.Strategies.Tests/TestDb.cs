using AlphaLab.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Strategies.Tests;

/// <summary>A throwaway on-disk SQLite store migrated to the latest schema — for the DummyRoster test
/// (the only 2.9 test that touches a DB). Mirrors the Data.Tests helper.</summary>
internal static class TestDb
{
    public static AlphaLabDbContext Open(string path) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options);

    public static string CreateMigrated()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-strat-" + Guid.NewGuid().ToString("N") + ".db");
        using var db = Open(path);
        db.Database.Migrate();
        return path;
    }

    public static void Delete(string path)
    {
        // P20 (finding 387): SAFE ONLY BECAUSE PARALLELIZATION IS DISABLED assembly-wide
        // ([assembly: CollectionBehavior(DisableTestParallelization = true)], TestParallelization.cs).
        // ClearAllPools() is PROCESS-GLOBAL - it disposes every pooled SQLite connection in the process,
        // including ones other test classes are still using. Serialized, there are no other classes in
        // flight and the call is harmless; re-enable parallelism and this line reintroduces a 1-in-3 flake
        // presenting as ObjectDisposedException inside Migrate(). The correct fix is Pooling=False on the
        // connection string, which makes the call unnecessary rather than merely safe - see PROGRESS's P20
        // entry for its named triggers.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(path + suffix)) File.Delete(path + suffix); } catch { /* best effort */ }
        }
    }
}
