using AlphaLab.Data;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The D72 liveness heartbeat (checkpoint 2.12): HeartbeatWriter.Beat stamps worker_state.heartbeat_at ONLY
/// while a run is in progress, and advances it on each tick — the property the stale-run guard relies on.
/// Standalone (a migrated temp DB + two fixed clocks) so "advances over time" is deterministic without a
/// real background thread.
/// </summary>
public sealed class HeartbeatTests : IDisposable
{
    private readonly string _dbPath;

    public HeartbeatTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"alphalab-hb-{Guid.NewGuid():N}.db");
        using var db = Open();
        db.Database.Migrate();
    }

    [Fact]
    public void Beat_WhileRunInProgress_AdvancesHeartbeat()
    {
        var t1 = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddSeconds(30);

        using (var db = Open())
        {
            db.WorkerState.Find(1)!.RunInProgress = 1;
            db.SaveChanges();
        }

        using (var db = Open())
        {
            Assert.True(new HeartbeatWriter(db, new FixedTimeProvider(t1)).Beat());
        }
        var first = ReadHeartbeat();

        using (var db = Open())
        {
            Assert.True(new HeartbeatWriter(db, new FixedTimeProvider(t2)).Beat());
        }
        var second = ReadHeartbeat();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);                       // it advanced
        Assert.True(string.CompareOrdinal(second, first) > 0); // …forward
    }

    [Fact]
    public void Beat_WhenIdle_DoesNothing()
    {
        // worker_state seeds run_in_progress=0; a beat between launches must not stamp (so it goes stale).
        using var db = Open();
        var beat = new HeartbeatWriter(db, new FixedTimeProvider(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero))).Beat();

        Assert.False(beat);
        Assert.Null(db.WorkerState.Find(1)!.HeartbeatAt);
    }

    private AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private string? ReadHeartbeat()
    {
        using var db = Open();
        return db.WorkerState.Find(1)!.HeartbeatAt;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }
}
