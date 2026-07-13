using AlphaLab.Data;
using AlphaLab.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// Phase-0 catch-up invariant (D47/D61): with no trading_calendar and zero committed runs, the
/// resolver reports "nothing to do" — the OnDemand launch then logs it and exits 0.
/// </summary>
public sealed class MissedSessionResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _cs;

    public MissedSessionResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"alphalab-worker-{Guid.NewGuid():N}.db");
        _cs = $"Data Source={_dbPath}";
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private AlphaLabDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(_cs).Options);

    [Fact]
    public async Task NoCalendar_NoRuns_ResolvesToNothingToDo()
    {
        await using var ctx = NewContext();
        var resolver = new Phase0MissedSessionResolver(ctx);

        var missed = await resolver.ResolveAsync();

        Assert.Empty(missed);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch (IOException) { /* harmless */ }
        }
    }
}
