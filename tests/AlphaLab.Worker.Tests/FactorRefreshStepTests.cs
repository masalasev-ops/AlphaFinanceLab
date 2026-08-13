using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The D41 refresh's CADENCE (checkpoint 6.6). `IsDue` is pure on its inputs, so the monthly rule is
/// testable without a clock and without a scheduler — which matters because the D61 default is OnDemand,
/// where no resident Quartz trigger exists to hold a monthly job. The due-date test IS the schedule.
/// </summary>
public class FactorRefreshStepTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"alphalab-frs-{Guid.NewGuid():N}.db");

    private static AlphaLabDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options);

    private static void TryDelete(string path)
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    private static AlphaLabDbContext Seeded(string path, params string[] refreshedAt)
    {
        using (var db = NewContext(path)) db.Database.Migrate();
        var ctx = NewContext(path);
        foreach (var r in refreshedAt)
        {
            ctx.FactorRefreshLog.Add(new FactorRefreshLogRow { RefreshedAt = r, Checksum = "x", RowsAdded = 1 });
        }
        ctx.SaveChanges();
        return ctx;
    }

    [Fact]
    public void FR5_D41_WithNoRefreshEverWritten_ItIsDue()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p);
            Assert.True(FactorRefreshStep.IsDue(db, "2026-08-01", new FactorDataOptions(), out var why));
            Assert.Contains("no refresh has ever written", why, StringComparison.Ordinal);
        }
        finally { TryDelete(p); }
    }

    /// <summary>MONTHLY, not daily. A second launch in the same month must not re-fetch — the library
    /// publishes with weeks of lag, so a daily pull would be pure waste against a third-party host.</summary>
    [Fact]
    public void FR5_D41_AlreadyRefreshedThisMonth_IsNotDueAgain()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p, "2026-08-05T02:00:00Z");
            Assert.False(FactorRefreshStep.IsDue(db, "2026-08-28", new FactorDataOptions(), out var why));
            Assert.Contains("already refreshed this month", why, StringComparison.Ordinal);
        }
        finally { TryDelete(p); }
    }

    [Fact]
    public void FR5_D41_ANewMonthBeforeTheConfiguredDay_IsNotYetDue()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p, "2026-08-05T02:00:00Z");
            Assert.False(FactorRefreshStep.IsDue(db, "2026-09-03", new FactorDataOptions { RefreshDayOfMonth = 5 }, out var why));
            Assert.Contains("before the configured day", why, StringComparison.Ordinal);
        }
        finally { TryDelete(p); }
    }

    [Fact]
    public void FR5_D41_ANewMonthOnOrAfterTheConfiguredDay_IsDue()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p, "2026-08-05T02:00:00Z");
            Assert.True(FactorRefreshStep.IsDue(db, "2026-09-05", new FactorDataOptions { RefreshDayOfMonth = 5 }, out _));
            Assert.True(FactorRefreshStep.IsDue(db, "2026-09-30", new FactorDataOptions { RefreshDayOfMonth = 5 }, out _));
        }
        finally { TryDelete(p); }
    }

    /// <summary>A YEAR boundary is the case an ordinal month-number comparison gets wrong: December to
    /// January decreases the month number while increasing the month. The comparison is on the ISO
    /// `yyyy-MM` prefix, so it orders correctly across the year.</summary>
    [Fact]
    public void FR5_D41_ADecemberToJanuaryBoundary_IsDue()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p, "2026-12-07T02:00:00Z");
            Assert.True(FactorRefreshStep.IsDue(db, "2027-01-05", new FactorDataOptions { RefreshDayOfMonth = 5 }, out var why));
            Assert.Equal("due", why);
        }
        finally { TryDelete(p); }
    }

    /// <summary>The latest log row decides, not the first — several refreshes accumulate over time and
    /// an unordered read would compare against an ancient one and refresh every launch.</summary>
    [Fact]
    public void FR5_D41_TheLatestRefreshDecides_NotTheOldest()
    {
        var p = TempDb();
        try
        {
            using var db = Seeded(p, "2026-06-05T02:00:00Z", "2026-07-06T02:00:00Z", "2026-08-05T02:00:00Z");
            Assert.False(FactorRefreshStep.IsDue(db, "2026-08-20", new FactorDataOptions(), out var why));
            Assert.Contains("2026-08", why, StringComparison.Ordinal);
        }
        finally { TryDelete(p); }
    }
}
