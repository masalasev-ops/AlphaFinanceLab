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
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(path + suffix)) File.Delete(path + suffix); } catch { /* best effort */ }
        }
    }
}
