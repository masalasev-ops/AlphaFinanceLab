using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-30 / D54 — the pure NYSE calendar generator, spot-validated against real published exchange dates
/// (MASTER §20.5 "spot-validated against ≥ 2 recent exchange notices"). Covers the rule-based holidays +
/// observance quirks, the half-day rules, and the curated one-off closures.
/// </summary>
public class NyseCalendarTests
{
    private static readonly IReadOnlyList<TradingCalendarRow> Cal = NyseCalendar.Generate(1996, 2026);
    private static readonly Dictionary<string, TradingCalendarRow> ByDate =
        Cal.ToDictionary(r => r.Date, StringComparer.Ordinal);

    private static bool IsSession(string iso) => ByDate.ContainsKey(iso);
    private static TradingCalendarRow Row(string iso) => ByDate[iso];

    [Fact]
    public void FullHolidays_2024_AreClosed()
    {
        string[] holidays2024 =
        [
            "2024-01-01", // New Year's Day (Mon)
            "2024-01-15", // MLK Day (3rd Mon Jan)
            "2024-02-19", // Washington's Birthday (3rd Mon Feb)
            "2024-03-29", // Good Friday
            "2024-05-27", // Memorial Day (last Mon May)
            "2024-06-19", // Juneteenth
            "2024-07-04", // Independence Day
            "2024-09-02", // Labor Day (1st Mon Sep)
            "2024-11-28", // Thanksgiving (4th Thu Nov)
            "2024-12-25", // Christmas
        ];
        foreach (var h in holidays2024) Assert.False(IsSession(h), $"{h} should be closed");
        Assert.True(IsSession("2024-03-28"));                 // a normal Thursday is a session
        Assert.Equal("full", Row("2024-03-28").Session);
    }

    [Fact]
    public void HalfDays_2024_CloseAt1300()
    {
        foreach (var h in new[] { "2024-07-03", "2024-11-29", "2024-12-24" }) // Jul 3, day after Thanksgiving, Xmas Eve
        {
            Assert.True(IsSession(h));
            Assert.Equal("half", Row(h).Session);
            Assert.Equal("13:00", Row(h).CloseTimeLocal);
        }
        Assert.Equal("16:00", Row("2024-12-26").CloseTimeLocal); // a normal full session closes 16:00
    }

    [Theory]
    [InlineData(2024, "2024-03-29")]
    [InlineData(2025, "2025-04-18")]
    [InlineData(2016, "2016-03-25")]
    public void GoodFriday_ComputedFromEaster(int _, string goodFriday)
    {
        Assert.False(IsSession(goodFriday));                          // Good Friday is closed
        Assert.True(IsSession(DateOnly.Parse(goodFriday).AddDays(-1).ToString("yyyy-MM-dd"))); // the Thursday trades
    }

    [Fact]
    public void NewYear_SaturdayJan1_DoesNotCloseThePriorFriday()
    {
        // Jan 1 2011 is a Saturday: NYSE does NOT close Fri Dec 31 2010, and there is no observed Monday.
        Assert.True(IsSession("2010-12-31"));
        Assert.Equal("full", Row("2010-12-31").Session);
        Assert.True(IsSession("2011-01-03")); // first 2011 session — no observed NYD holiday
    }

    [Fact]
    public void NewYear_SundayJan1_ShiftsToMonday()
    {
        // Jan 1 2023 is a Sunday -> observed Monday Jan 2 2023.
        Assert.False(IsSession("2023-01-02"));
        Assert.True(IsSession("2023-01-03"));
    }

    [Fact]
    public void Christmas_SaturdayDec25_ShiftsToFriday_AsFullNotHalf()
    {
        // Dec 25 2010 is a Saturday -> observed Fri Dec 24 (a FULL holiday, so no Christmas-Eve half-day).
        Assert.False(IsSession("2010-12-24"));
        Assert.True(IsSession("2010-12-23"));
        Assert.Equal("full", Row("2010-12-23").Session); // the 23rd is a normal full session, not an early close
    }

    [Fact]
    public void Independence_SundayJul4_ShiftsToMonday_NoJul3Half()
    {
        // Jul 4 2021 is a Sunday -> observed Monday Jul 5; Jul 2 (Fri) is a normal FULL session (no half).
        Assert.False(IsSession("2021-07-05"));
        Assert.True(IsSession("2021-07-02"));
        Assert.Equal("full", Row("2021-07-02").Session);
    }

    [Fact]
    public void MlkDay_NotObservedBefore1998()
    {
        Assert.True(IsSession("1997-01-20"));  // 3rd Mon Jan 1997 — traded (NYSE adopted MLK in 1998)
        Assert.False(IsSession("1998-01-19")); // 3rd Mon Jan 1998 — first observed MLK Day
    }

    [Fact]
    public void Juneteenth_NotObservedBefore2022()
    {
        Assert.True(IsSession("2021-06-18"));  // 2021 — no Juneteenth holiday
        Assert.False(IsSession("2022-06-20")); // Jun 19 2022 is Sunday -> first observed Monday Jun 20
    }

    [Fact]
    public void SpecialOneOffClosures_AreClosed_AndSurroundingSessionsTrade()
    {
        foreach (var closed in new[]
                 {
                     "2001-09-11", "2001-09-12", "2001-09-13", "2001-09-14", // 9/11
                     "2004-06-11",                                           // Reagan
                     "2007-01-02",                                           // Ford
                     "2012-10-29", "2012-10-30",                             // Sandy
                     "2018-12-05",                                           // Bush
                     "2025-01-09",                                           // Carter
                 })
        {
            Assert.False(IsSession(closed), $"{closed} should be a one-off closure");
        }
        Assert.True(IsSession("2001-09-17")); // reopened after 9/11
        Assert.True(IsSession("2012-10-31")); // reopened after Sandy
        Assert.True(IsSession("2007-01-03")); // day after the Ford closure
    }

    [Fact]
    public void Weekends_AreNotSessions()
    {
        Assert.False(IsSession("2024-03-30")); // Saturday
        Assert.False(IsSession("2024-03-31")); // Sunday
    }

    // Half-days count as sessions here — the ERA-SPLIT July 4 early close (Thursday July 4): before 2013
    // the half-day is the following Friday July 5 and July 3 (Wed) is a full session; from 2013 the early
    // close is Wednesday July 3 and July 5 is full.
    [Fact]
    public void Independence_ThursdayJul4_EarlyCloseMovedToJul3In2013()
    {
        Assert.Equal("full", Row("2002-07-03").Session); // pre-2013
        Assert.Equal("half", Row("2002-07-05").Session);
        Assert.Equal("13:00", Row("2002-07-05").CloseTimeLocal);
        Assert.Equal("full", Row("1996-07-03").Session);
        Assert.Equal("half", Row("1996-07-05").Session);

        Assert.Equal("half", Row("2024-07-03").Session); // 2013+
        Assert.Equal("full", Row("2024-07-05").Session);
    }

    [Fact]
    public void AnnualSessionCount_MatchesRealNyseTotals()
    {
        // Exact real NYSE annual session totals for the quirk / special-closure years — a tight guard a
        // loose range would miss (a mis-dated holiday or wrongly-applied closure shifts the count by 1-2).
        var expected = new Dictionary<int, int>
        {
            [2001] = 248, // 9/11 week closed
            [2004] = 252, // Reagan national day of mourning
            [2007] = 251, // Ford national day of mourning
            [2012] = 250, // Hurricane Sandy
            [2018] = 251, // Bush national day of mourning
            [2022] = 251, // Jan 1 Saturday — the New Year quirk (no observed holiday)
            [2024] = 252,
            [2025] = 250, // Carter national day of mourning
        };
        foreach (var (year, count) in expected)
        {
            Assert.Equal(count, Cal.Count(r => r.Date.StartsWith(year.ToString(), StringComparison.Ordinal)));
        }

        for (var year = 1996; year <= 2026; year++)
        {
            var n = Cal.Count(r => r.Date.StartsWith(year.ToString(), StringComparison.Ordinal));
            Assert.InRange(n, 245, 254); // backstop for the remaining years
        }
    }

    [Fact]
    public void Generate_RejectsInvertedRange()
    {
        Assert.Throws<ArgumentException>(() => NyseCalendar.Generate(2020, 2019));
    }
}
