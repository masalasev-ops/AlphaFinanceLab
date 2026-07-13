using AlphaLab.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Shared helper for the Phase-1 data tests: a throwaway on-disk SQLite file migrated to the latest
/// schema. On-disk (not in-memory) so partial-unique indexes, CHECKs, and cross-context reads behave
/// exactly as production. Callers dispose contexts and call <see cref="Delete"/> in a finally.
/// </summary>
internal static class TestDb
{
    public static string NewPath() =>
        Path.Combine(Path.GetTempPath(), "alphalab-t-" + Guid.NewGuid().ToString("N") + ".db");

    public static AlphaLabDbContext Open(string path) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options);

    /// <summary>Create a fresh temp DB migrated to the latest schema; returns its path.</summary>
    public static string CreateMigrated()
    {
        var path = NewPath();
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
