using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-30 / D54 — the seeded trading calendar + ICalendarService. FX-HolidayOutage (catch-up recovers only
/// real sessions across a holiday weekend, never a fabricated day) and FX-HalfDay (the trigger keys off the
/// 13:00 close) per TEST_PLAN §2, plus the seeder's idempotency and the session-navigation surface.
/// </summary>
public class CalendarServiceTests
{
    private static string Seed(out string path)
    {
        path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new CalendarSeeder(db).Seed(2023, 2025);
        return path;
    }

    private static void WithCalendar(Action<ICalendarService> assert)
    {
        var path = Seed(out _);
        try
        {
            using var db = TestDb.Open(path);
            assert(new CalendarService(db));
        }
        finally { TestDb.Delete(path); }
    }

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Seeder_IsIdempotent()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            int first, second, count;
            using (var db = TestDb.Open(path)) first = new CalendarSeeder(db).Seed(2023, 2025);
            using (var db = TestDb.Open(path)) second = new CalendarSeeder(db).Seed(2023, 2025);
            using (var db = TestDb.Open(path)) count = db.TradingCalendar.Count();

            Assert.True(first > 740);        // ~250 sessions/yr x 3 yrs
            Assert.Equal(0, second);         // a re-seed writes nothing
            Assert.Equal(first, count);      // no duplicates
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void IsTradingDay_DistinguishesSessionsFromHolidays()
    {
        WithCalendar(cal =>
        {
            Assert.False(cal.IsTradingDay(D(2024, 12, 25))); // Christmas
            Assert.True(cal.IsTradingDay(D(2024, 12, 26)));  // the session after
            Assert.False(cal.IsTradingDay(D(2024, 11, 30))); // Saturday
        });
    }

    // FX-HalfDay: the day after Thanksgiving 2024 closes at 13:00; the orchestrator trigger keys off it.
    [Fact]
    public void FX_HalfDay_EarlyCloseSurfacesAs1300()
    {
        WithCalendar(cal =>
        {
            Assert.True(cal.IsHalfDay(D(2024, 11, 29)));
            Assert.Equal("13:00", cal.CloseTime(D(2024, 11, 29)));

            Assert.False(cal.IsHalfDay(D(2024, 12, 26)));
            Assert.Equal("16:00", cal.CloseTime(D(2024, 12, 26)));

            Assert.Null(cal.CloseTime(D(2024, 12, 25))); // not a session -> no close time
        });
    }

    // FX-HolidayOutage: an outage from Wed 2024-11-27 through Mon 2024-12-02 recovers ONLY real sessions —
    // Thanksgiving (11-28) and the weekend (11-30, 12-01) are never fabricated.
    [Fact]
    public void FX_HolidayOutage_SessionsBetweenSkipsHolidayAndWeekend()
    {
        WithCalendar(cal =>
        {
            var missed = cal.SessionsBetween(D(2024, 11, 28), D(2024, 12, 2)); // sessions after the last processed (11-27)
            Assert.Equal([D(2024, 11, 29), D(2024, 12, 2)], missed);

            var inclusive = cal.SessionsBetween(D(2024, 11, 27), D(2024, 12, 2));
            Assert.Equal([D(2024, 11, 27), D(2024, 11, 29), D(2024, 12, 2)], inclusive);
        });
    }

    [Fact]
    public void PreviousAndNextSession_StepAcrossTheHolidayWeekend()
    {
        WithCalendar(cal =>
        {
            // Thanksgiving 2024-11-28 is not a session; its neighbours are 11-27 and 11-29 (half-day).
            Assert.Equal(D(2024, 11, 27), cal.PreviousSession(D(2024, 11, 28)));
            Assert.Equal(D(2024, 11, 29), cal.NextSession(D(2024, 11, 28)));

            // From a session date the navigation is STRICT (not the date itself).
            Assert.Equal(D(2024, 11, 27), cal.PreviousSession(D(2024, 11, 29)));
            Assert.Equal(D(2024, 12, 2), cal.NextSession(D(2024, 11, 29)));
        });
    }

    [Fact]
    public void SessionsBetween_EdgeCases()
    {
        WithCalendar(cal =>
        {
            Assert.Empty(cal.SessionsBetween(D(2024, 11, 30), D(2024, 12, 1))); // a pure weekend
            Assert.Empty(cal.SessionsBetween(D(2024, 12, 2), D(2024, 11, 28))); // inverted range
        });
    }

    [Fact]
    public void Navigation_OutsideSeededRange_ReturnsNull()
    {
        WithCalendar(cal =>
        {
            Assert.Null(cal.PreviousSession(D(2023, 1, 3)));  // 2023-01-03 is the first seeded session
            Assert.Null(cal.NextSession(D(2025, 12, 31)));    // 2025-12-31 is the last seeded session
        });
    }
}
