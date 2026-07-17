using System.Globalization;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The D47 missed-session resolver (checkpoint 2.11): which forward sessions still need processing, in
/// order. The runs table IS the state — this is a pure query. Covers the empty-store no-op (the Phase-0
/// behaviour the real resolver subsumes), the fresh-store gap from the backfill watermark, the
/// already-caught-up no-op, the ET close-time guard (before/after close), and sequential resume.
/// </summary>
public sealed class MissedSessionResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IReadOnlyList<string> _sessions;

    public MissedSessionResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"alphalab-resolver-{Guid.NewGuid():N}.db");
        _sessions = BuildSessions(new DateOnly(2024, 1, 1), 30);
        using var db = Open();
        db.Database.Migrate();
        foreach (var s in _sessions)
            db.TradingCalendar.Add(new TradingCalendarRow { Date = s, Session = "full", CloseTimeLocal = "16:00" });
        db.SaveChanges();
    }

    // ---- the Phase-0 behaviour the real resolver subsumes ----
    [Fact]
    public async Task EmptyStore_NoBarsNoRuns_ResolvesToNothing()
    {
        var missed = await Resolve(At(_sessions[20], 22)); // clock well past several closes
        Assert.Empty(missed); // no bars ⇒ no anchor ⇒ nothing to catch up
    }

    // ---- fresh store: forward operation starts after the backfill watermark ----
    [Fact]
    public async Task FreshStore_ReplaysGapFromBackfillWatermark_ThroughLastCompleted()
    {
        SeedBarsThrough(10);                       // backfill watermark = sessions[10]
        var missed = await Resolve(At(_sessions[14], 22)); // now = past sessions[14]'s close

        Assert.Equal(Days(11, 14), Iso(missed));   // the four-session gap, in order
    }

    // ---- caught up: every session through the last completed has an ok forward run ----
    [Fact]
    public async Task AllCaughtUp_ResolvesToNothing()
    {
        SeedBarsThrough(14);
        for (var i = 11; i <= 14; i++) AddOkRun(i, "catchup");
        var missed = await Resolve(At(_sessions[14], 22));

        Assert.Empty(missed);
    }

    // ---- the ET guard: today's session is not "completed" until its close has passed ----
    [Fact]
    public async Task TodaysSession_BeforeItsClose_IsNotYetCompleted()
    {
        SeedBarsThrough(13);
        AddOkRun(11, "catchup"); AddOkRun(12, "catchup"); AddOkRun(13, "catchup");
        // now = sessions[14] at 12:00 UTC (07:00 ET) — BEFORE the 16:00 ET close.
        var missed = await Resolve(At(_sessions[14], 12));

        Assert.Empty(missed); // last completed is sessions[13] (already ok); sessions[14] excluded
    }

    [Fact]
    public async Task TodaysSession_AfterItsClose_IsIncluded()
    {
        SeedBarsThrough(13);
        AddOkRun(11, "catchup"); AddOkRun(12, "catchup"); AddOkRun(13, "catchup");
        // now = sessions[14] at 22:00 UTC (past close) — sessions[14] is now the last completed.
        var missed = await Resolve(At(_sessions[14], 22));

        Assert.Equal(Days(14, 14), Iso(missed));
    }

    // ---- sequential resume: a failed day left no ok row + rolled back its bars, so the anchor stays put ----
    [Fact]
    public async Task Resume_AfterAFailedDay_StartsAtTheFailedDay()
    {
        // ok through sessions[12]; sessions[13] failed (no ok row, its bars rolled back), so maxBar = 12.
        SeedBarsThrough(12);
        AddOkRun(11, "catchup"); AddOkRun(12, "catchup");
        var missed = await Resolve(At(_sessions[15], 22));

        Assert.Equal(Days(13, 15), Iso(missed)); // resumes at 13
    }

    // ---- helpers ----

    private AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private async Task<IReadOnlyList<DateOnly>> Resolve(DateTimeOffset now)
    {
        using var db = Open();
        var resolver = new MissedSessionResolver(db, new CalendarService(db), new FixedTimeProvider(now));
        return await resolver.ResolveAsync();
    }

    private void SeedBarsThrough(int lastIndex)
    {
        using var db = Open();
        for (var i = 0; i <= lastIndex; i++)
        {
            db.Bars.Add(new BarRow
            {
                SecurityId = 1, Date = _sessions[i], Version = 1, ObservedAt = $"{_sessions[i]}T22:00:00Z",
                Close = 100, AdjClose = 100, Volume = 1_000_000, Source = "eodhd",
            });
        }
        db.SaveChanges();
    }

    private void AddOkRun(int index, string runKind)
    {
        using var db = Open();
        db.Runs.Add(new RunRow
        {
            AsOf = _sessions[index], RunKind = runKind, Watermark = $"{_sessions[index]}T22:00:00Z",
            StartedAt = "t", FinishedAt = "t", Status = "ok",
        });
        db.SaveChanges();
    }

    private DateTimeOffset At(string isoDate, int utcHour) => new(
        DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(new TimeOnly(utcHour, 0)),
        TimeSpan.Zero);

    private string[] Days(int fromIndex, int toIndex)
    {
        var list = new List<string>();
        for (var i = fromIndex; i <= toIndex; i++) list.Add(_sessions[i]);
        return list.ToArray();
    }

    private static string[] Iso(IReadOnlyList<DateOnly> days) =>
        days.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();

    private static IReadOnlyList<string> BuildSessions(DateOnly start, int count)
    {
        var list = new List<string>(count);
        var d = start;
        while (list.Count < count)
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                list.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            d = d.AddDays(1);
        }
        return list;
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
