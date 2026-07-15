using System.Globalization;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Providers;

/// <summary>
/// Pure, deterministic generator for the NYSE trading calendar (FR-30 / D54 / MASTER §20.5). It encodes
/// the exchange's published holiday rules and known half-days (13:00 ET closes) — the "generation script"
/// the seed runs, and the fail-safe fallback ("regenerate from rules"). No I/O: <see cref="Generate"/>
/// returns <see cref="TradingCalendarRow"/>s for [fromYear, toYear] which the seeder writes to
/// <c>trading_calendar</c>. Every rule below is spot-validated against real exchange dates in the tests.
///
/// Rules encoded:
///  - Full holidays: New Year's Day, MLK Day (3rd Mon Jan, NYSE only since 1998), Washington's Birthday
///    (3rd Mon Feb), Good Friday (Easter − 2, Computus), Memorial Day (last Mon May), Juneteenth (Jun 19,
///    NYSE only since 2022), Independence Day (Jul 4), Labor Day (1st Mon Sep), Thanksgiving (4th Thu Nov),
///    Christmas (Dec 25).
///  - Observance: a fixed-date holiday on Saturday shifts to the preceding Friday, on Sunday to the
///    following Monday — EXCEPT New Year's Day, where NYSE does NOT close the preceding Friday for a
///    Saturday Jan 1 (only the Sunday→Monday shift applies). This is the well-known NYSE quirk.
///  - Half-days (early 13:00 ET close): the day after Thanksgiving (always); Christmas Eve (Dec 24) when
///    it is a Mon–Thu session; and the Independence-Day early close, which is ERA-SPLIT — July 3 when it
///    is a Mon/Tue/Thu, but for a Thursday July 4 (Wednesday July 3) the early close was the FOLLOWING
///    Friday July 5 before 2013 and moved to Wednesday July 3 from 2013 on (per exchange_calendars).
///    The 13:00 close time is assumed across the seeded window; a pre-1996 backfill (2 pm-era closes)
///    would revisit that assumption (stop-and-report seam).
///  - Special one-off full closures (national mourning, disasters, 9/11) — a curated, cited list; these
///    cannot be derived from rules. Future one-offs are unknowable and are added by a re-seed / the D55
///    admin path when announced (fail closed — never fabricated).
/// </summary>
public static class NyseCalendar
{
    private const string FullClose = "16:00";
    private const string HalfClose = "13:00";

    /// <summary>NYSE first observed MLK Day in 1998.</summary>
    private const int MlkFirstYear = 1998;

    /// <summary>NYSE first observed Juneteenth in 2022.</summary>
    private const int JuneteenthFirstYear = 2022;

    /// <summary>From 2013 the Thursday-July-4 early close moved to Wednesday July 3; before then it was
    /// the following Friday July 5 (exchange_calendars).</summary>
    private const int Jul3WednesdayRuleFirstYear = 2013;

    /// <summary>The last calendar year through which <see cref="SpecialClosures"/> has been reviewed against
    /// exchange notices (its newest entry is 2025-01-09, the Carter national day of mourning). A documentary
    /// constant, NOT a config key — the closure list is code, not config, so a config key would violate rule
    /// 14 (mirrors <see cref="Http.FileRawCache.RetentionDays"/>). <c>--preflight</c> warns when a run's
    /// as-of year exceeds this, because the generated calendar would confidently assert trading days for any
    /// closure since — a stale-list gap the quality gate would misreport as a provider gap. Bump this (and
    /// add the closures) when the list is next reviewed forward.</summary>
    public const int SpecialClosuresReviewedThroughYear = 2025;

    /// <summary>
    /// One-off full closures that no holiday rule produces (national days of mourning, disasters, the
    /// Sept-11 shutdown). Cited so the list is auditable; completeness is a known limitation (stop-and-
    /// report seam) — a missing historical closure surfaces as a replay trading a day the market was shut.
    /// Reviewed through <see cref="SpecialClosuresReviewedThroughYear"/>.
    /// </summary>
    private static readonly HashSet<string> SpecialClosures = new(StringComparer.Ordinal)
    {
        "2001-09-11", "2001-09-12", "2001-09-13", "2001-09-14", // September 11 attacks (reopened Sep 17)
        "2004-06-11",                                           // Ronald Reagan — national day of mourning
        "2007-01-02",                                           // Gerald Ford — national day of mourning
        "2012-10-29", "2012-10-30",                             // Hurricane Sandy
        "2018-12-05",                                           // George H. W. Bush — national day of mourning
        "2025-01-09",                                           // Jimmy Carter — national day of mourning
    };

    /// <summary>Generate every NYSE session in [fromYear, toYear] inclusive, ordered by date.</summary>
    public static IReadOnlyList<TradingCalendarRow> Generate(int fromYear, int toYear)
    {
        if (toYear < fromYear) throw new ArgumentException($"toYear ({toYear}) precedes fromYear ({fromYear}).");

        var rows = new List<TradingCalendarRow>((toYear - fromYear + 1) * 253);
        for (var year = fromYear; year <= toYear; year++)
        {
            var holidays = FullDayHolidays(year);
            var halfDays = HalfDays(year, holidays);

            var day = new DateOnly(year, 1, 1);
            var end = new DateOnly(year, 12, 31);
            for (; day <= end; day = day.AddDays(1))
            {
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                var iso = Iso(day);
                if (holidays.Contains(day) || SpecialClosures.Contains(iso)) continue;

                var half = halfDays.Contains(day);
                rows.Add(new TradingCalendarRow
                {
                    Date = iso,
                    Session = half ? "half" : "full",
                    CloseTimeLocal = half ? HalfClose : FullClose
                });
            }
        }
        return rows;
    }

    // ---- Full-day holidays (observed dates) for one year ----
    private static HashSet<DateOnly> FullDayHolidays(int year)
    {
        var h = new HashSet<DateOnly>
        {
            NthWeekday(year, 2, DayOfWeek.Monday, 3),          // Washington's Birthday
            EasterSunday(year).AddDays(-2),                    // Good Friday
            LastWeekday(year, 5, DayOfWeek.Monday),            // Memorial Day
            ObservedFixed(new DateOnly(year, 7, 4)),           // Independence Day
            NthWeekday(year, 9, DayOfWeek.Monday, 1),          // Labor Day
            NthWeekday(year, 11, DayOfWeek.Thursday, 4),       // Thanksgiving
            ObservedFixed(new DateOnly(year, 12, 25)),         // Christmas
        };

        // New Year's Day — only the Sunday→Monday shift (Saturday Jan 1 does NOT close the prior Friday).
        var nyd = new DateOnly(year, 1, 1);
        if (nyd.DayOfWeek == DayOfWeek.Sunday) h.Add(nyd.AddDays(1));
        else if (nyd.DayOfWeek != DayOfWeek.Saturday) h.Add(nyd);

        if (year >= MlkFirstYear) h.Add(NthWeekday(year, 1, DayOfWeek.Monday, 3));         // MLK Day
        if (year >= JuneteenthFirstYear) h.Add(ObservedFixed(new DateOnly(year, 6, 19)));  // Juneteenth

        return h;
    }

    // ---- Half-day (13:00 close) sessions for one year ----
    private static HashSet<DateOnly> HalfDays(int year, HashSet<DateOnly> holidays)
    {
        var half = new HashSet<DateOnly>();

        // Day after Thanksgiving (the 4th-Thursday + 1 = Friday) — always an early close.
        AddHalf(half, holidays, NthWeekday(year, 11, DayOfWeek.Thursday, 4).AddDays(1));

        AddIndependenceEarlyClose(half, holidays, year);

        // Christmas Eve is an early close when Dec 24 is a Mon–Thu session (Dec 25 a normal weekday
        // holiday); a Sat/Sun-observed Christmas makes Dec 24 the full holiday or a weekend, so no half.
        AddEve(half, holidays, new DateOnly(year, 12, 24), new DateOnly(year, 12, 25));

        return half;
    }

    // The Independence-Day early close is era-split. July 3 is the half-day when it is a Mon/Tue/Thu
    // (both eras). When July 4 is a THURSDAY (July 3 a Wednesday) the early close was the FOLLOWING
    // Friday July 5 before 2013, and moved to Wednesday July 3 from 2013 on (exchange_calendars). A
    // Sat/Sun July 4 (weekend-observed) has no July 3 early close.
    private static void AddIndependenceEarlyClose(HashSet<DateOnly> half, HashSet<DateOnly> holidays, int year)
    {
        var jul4 = new DateOnly(year, 7, 4);
        if (jul4.DayOfWeek == DayOfWeek.Thursday)
        {
            AddHalf(half, holidays, year >= Jul3WednesdayRuleFirstYear
                ? new DateOnly(year, 7, 3)   // Wednesday July 3 (2013+)
                : new DateOnly(year, 7, 5));  // Friday July 5 (pre-2013)
        }
        else
        {
            AddEve(half, holidays, new DateOnly(year, 7, 3), jul4);
        }
    }

    private static void AddEve(HashSet<DateOnly> half, HashSet<DateOnly> holidays, DateOnly eve, DateOnly holiday)
    {
        if (holiday.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return; // holiday observed off-day
        AddHalf(half, holidays, eve);
    }

    private static void AddHalf(HashSet<DateOnly> half, HashSet<DateOnly> holidays, DateOnly day)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return; // no weekend session
        if (holidays.Contains(day)) return;                                  // a full holiday, not a half-day
        half.Add(day);
    }

    // ---- Date helpers ----

    /// <summary>Fixed-date observance: Saturday→preceding Friday, Sunday→following Monday, else itself.</summary>
    private static DateOnly ObservedFixed(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => d.AddDays(-1),
        DayOfWeek.Sunday => d.AddDays(1),
        _ => d
    };

    /// <summary>The n-th <paramref name="dow"/> of the month (n = 1 is the first).</summary>
    private static DateOnly NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)dow - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    /// <summary>The last <paramref name="dow"/> of the month.</summary>
    private static DateOnly LastWeekday(int year, int month, DayOfWeek dow)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)dow + 7) % 7;
        return last.AddDays(-offset);
    }

    /// <summary>Gregorian Easter Sunday (the anonymous Computus). Good Friday is two days earlier.</summary>
    private static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var hh = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - hh - k) % 7;
        var m = (a + 11 * hh + 22 * l) / 451;
        var month = (hh + l - 7 * m + 114) / 31;
        var day = ((hh + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
