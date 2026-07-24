using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The IFeatureView adapter over the versioned bar store. Carries F-LEAK for the feature path: every
/// value read at asOf must be byte-identical whether or not rows dated AFTER asOf are visible at the
/// watermark. That is the property the whole point-in-time claim (rule 4) rests on, and the one a
/// replay pinned to the past needs in order to reproduce.
/// </summary>
public class BarFeatureViewTests
{
    private const long Aapl = 1;
    private const long Msft = 2;

    // A short synthetic NYSE-like run of sessions. The calendar is seeded from the real seeder, so
    // these are genuine sessions rather than "every weekday".
    private static readonly DateOnly AsOf = new(2026, 6, 30);

    private static readonly string ObservedAtRun = "2026-06-30T22:00:00Z";  // when the asOf-day data was seen
    private static readonly string ObservedLater = "2026-07-10T22:00:00Z";  // when the FUTURE bars were seen

    private static readonly string WatermarkBefore = "2026-06-30T23:00:00Z"; // future rows invisible
    private static readonly string WatermarkAfter = "2026-07-11T23:00:00Z";  // future rows visible

    private static CostsOptions Costs() => new();

    /// <summary>Seed 60 sessions of bars ending at AsOf for two securities, then (optionally) seed bars
    /// for the sessions AFTER AsOf — observed later, so a watermark can hide or reveal them.</summary>
    private static string SeededDb(bool withFutureBars)
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new CalendarSeeder(db).Seed(2026, 2026);

        var sm = new SecurityMaster(db);
        sm.Register("AAPL", "US", "2020-01-01"); // security_id 1
        sm.Register("MSFT", "US", "2020-01-01"); // security_id 2

        var calendar = new CalendarService(db);
        var ingest = new BarIngestionService(db);

        var history = SessionsEndingAt(calendar, AsOf, 60);
        ingest.IngestEod(Aapl, history.Select((d, i) => Bar(d, 100.0 + i)).ToList(), ObservedAtRun);
        ingest.IngestEod(Msft, history.Select((d, i) => Bar(d, 200.0 + i)).ToList(), ObservedAtRun);

        if (withFutureBars)
        {
            // Sessions strictly after AsOf, at wildly different prices. If any of these ever leaks into
            // an asOf read, the assertions below will not be subtle about it.
            var future = calendar.SessionsBetween(AsOf.AddDays(1), new DateOnly(2026, 7, 10));
            ingest.IngestEod(Aapl, future.Select(d => Bar(d, 9_999.0)).ToList(), ObservedLater);
            ingest.IngestEod(Msft, future.Select(d => Bar(d, 8_888.0)).ToList(), ObservedLater);
        }

        return path;
    }

    private static EodBar Bar(DateOnly date, double close) => new(
        Date: date.ToString("yyyy-MM-dd"),
        Open: close - 0.5,
        High: close + 1.0,
        Low: close - 1.0,
        Close: close,
        AdjClose: close,
        Volume: 1_000_000);

    private static List<DateOnly> SessionsEndingAt(ICalendarService calendar, DateOnly asOf, int count)
    {
        var sessions = new List<DateOnly>();
        var cursor = calendar.IsTradingDay(asOf) ? asOf : calendar.PreviousSession(asOf);
        while (cursor is not null && sessions.Count < count)
        {
            sessions.Add(cursor.Value);
            cursor = calendar.PreviousSession(cursor.Value);
        }
        sessions.Reverse();
        return sessions;
    }

    private static BarFeatureView View(AlphaLabDbContext db, string watermark) =>
        new(new BarReadService(db), new CalendarService(db), AsOf, watermark, Costs());

    // finding 275 — the carry-forward mark for a data gap. LastRawCloseOnOrBefore returns the most recent raw
    // close on or before AsOf, so a held name missing ITS bar on this session (but priced the day before) marks
    // at the last known price, never a years-old cost basis. Null only when the name was never priced ≤ AsOf.
    [Fact]
    public void LastRawCloseOnOrBefore_CarriesForwardThePriorBar_NullWhenNeverPriced()
    {
        var path = SeededDb(withFutureBars: false);   // AAPL/MSFT bars on every session ENDING AT AsOf
        try
        {
            using var db = TestDb.Open(path);
            var view = View(db, WatermarkAfter);
            var calendar = new CalendarService(db);

            // AAPL is priced ON AsOf ⇒ the lookup returns AsOf's own close (== the last stored ≤ AsOf).
            var aaplToday = view.RawClose(new SecurityId(Aapl), AsOf);
            Assert.NotNull(aaplToday);
            Assert.Equal(aaplToday, view.LastRawCloseOnOrBefore(new SecurityId(Aapl)));

            // Introduce a DATA GAP: overwrite AsOf so AAPL has no visible bar there — mimicking OEF 2014-04-22.
            // (Delete is only in this throwaway test DB; the store's append-only rule is a production invariant.)
            db.Bars.RemoveRange(db.Bars.Where(b => b.SecurityId == Aapl && b.Date == AsOf.ToString("yyyy-MM-dd")));
            db.SaveChanges();
            var gapped = View(db, WatermarkAfter);
            var prevClose = gapped.RawClose(new SecurityId(Aapl), calendar.PreviousSession(AsOf)!.Value);

            Assert.Null(gapped.RawClose(new SecurityId(Aapl), AsOf));              // no bar today
            Assert.Equal(prevClose, gapped.LastRawCloseOnOrBefore(new SecurityId(Aapl)));  // carries the prior close forward

            // A never-priced security ⇒ null (the conservative cost-basis fallback fires downstream).
            Assert.Null(gapped.LastRawCloseOnOrBefore(new SecurityId(999)));
        }
        finally { TestDb.Delete(path); }
    }

    // ============================ F-LEAK ============================

    /// <summary>
    /// F-LEAK. Every feature at asOf, computed twice: once under a watermark that cannot see the bars
    /// dated after asOf, once under one that can. The two must agree exactly.
    ///
    /// This is the test that would catch the whole class of "reads around the reader" bugs — a series
    /// window that used the LAST 21 bars rather than the last 21 SESSIONS ENDING AT asOf would pull
    /// tomorrow's 9,999 print into today's ADV and vol, and every number downstream would be quietly
    /// wrong on exactly the days that mattered.
    /// </summary>
    [Fact]
    public void FLEAK_EveryFeatureAtAsOf_IsIdenticalWhetherOrNotFutureRowsAreVisible()
    {
        // ONE store holding the future bars, read under two watermarks — so the watermark genuinely
        // hides them in the first read and reveals them in the second. (Comparing two DIFFERENT stores
        // would prove nothing: the blind one has no future rows to leak, so both reads would agree for
        // a reason that has nothing to do with the watermark.)
        var path = SeededDb(withFutureBars: true);
        try
        {
            using var db = TestDb.Open(path);
            var id = new SecurityId(Aapl);

            var blind = View(db, WatermarkBefore);   // future rows not yet observed
            var sighted = View(db, WatermarkAfter);  // future rows visible

            // Sanity: the watermark really is what separates these two views — the future rows are
            // invisible at one and visible at the other. Without this the equalities below are vacuous.
            // (Asked of the reader directly: the view itself refuses a future date outright.)
            var reader = new BarReadService(db);
            Assert.Empty(reader.GetCrossSection("2026-07-02", WatermarkBefore));
            Assert.NotEmpty(reader.GetCrossSection("2026-07-02", WatermarkAfter));

            Assert.Equal(blind.AdjClose(id, AsOf), sighted.AdjClose(id, AsOf));
            Assert.Equal(blind.RawClose(id, AsOf), sighted.RawClose(id, AsOf));
            Assert.Equal(blind.RawOpen(id, AsOf), sighted.RawOpen(id, AsOf));
            Assert.Equal(blind.AdjCloseSeries(id, 21), sighted.AdjCloseSeries(id, 21));
            Assert.Equal(blind.Adv21Shares(id), sighted.Adv21Shares(id));
            Assert.Equal(blind.Adv21Notional(id), sighted.Adv21Notional(id));
            Assert.Equal(blind.RealizedVolDaily(id, 21), sighted.RealizedVolDaily(id, 21));
            Assert.Equal(blind.PricedOn(AsOf), sighted.PricedOn(AsOf));

            // And the shared answer is the asOf one, not the future one — proving the equalities above are
            // not two views agreeing on the same wrong number.
            Assert.Equal(159.0, sighted.RawClose(id, AsOf)); // 100 + 59 (the 60th and last session)
            Assert.DoesNotContain(9_999.0, sighted.AdjCloseSeries(id, 21));
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// The other half of the watermark contract, and the reason it is a SEPARATE test: a later-observed
    /// CORRECTION to an asOf-dated bar is not a leak — it is D40 working, and the two watermarks must
    /// DISAGREE about it. (A run pinned to the old watermark still reproduces byte-identically; that is
    /// the property, not "the answer never changes".)
    ///
    /// What this actually guards: that the view reads THROUGH IBarReadService rather than around it. A
    /// view that ordered by version without filtering observed_at — or that cached across watermarks —
    /// would show the old run tomorrow's correction and pass every test above, because those rows are
    /// dated at asOf and the date bound never touches them.
    /// </summary>
    [Fact]
    public void FR4_ALaterObservedCorrectionToAnAsOfBar_IsVisibleOnlyAtTheLaterWatermark()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using (var seed = TestDb.Open(path))
            {
                // A v2 correction to the asOf-dated bar itself: same date, observed 10 days later.
                new BarIngestionService(seed).IngestEod(Aapl, [Bar(AsOf, 777.0)], ObservedLater);
            }

            using var db = TestDb.Open(path);
            var id = new SecurityId(Aapl);

            Assert.Equal(159.0, View(db, WatermarkBefore).RawClose(id, AsOf)); // the run as it happened
            Assert.Equal(777.0, View(db, WatermarkAfter).RawClose(id, AsOf));  // the corrected view
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>Rule 4: asking for a date after asOf is a defect, not an absence. It throws rather than
    /// returning null, because null would be indistinguishable from thin history — which callers are
    /// specifically written to tolerate (catalog §2), so a leak bug would be silently absorbed.</summary>
    [Fact]
    public void FR4_AskingForADateAfterAsOf_Throws_RatherThanReturningNull()
    {
        var path = SeededDb(withFutureBars: true);
        try
        {
            using var db = TestDb.Open(path);
            var view = View(db, WatermarkAfter);
            var id = new SecurityId(Aapl);
            var tomorrow = AsOf.AddDays(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => view.AdjClose(id, tomorrow));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.RawClose(id, tomorrow));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.RawOpen(id, tomorrow));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.PricedOn(tomorrow));
        }
        finally { TestDb.Delete(path); }
    }

    // ============================ the date-major read (D78) ============================

    [Fact]
    public void D78_PricedOn_ReturnsEveryNameWithAVisibleBar_OrderedById()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using var db = TestDb.Open(path);

            var priced = View(db, WatermarkBefore).PricedOn(AsOf);

            Assert.Equal([new SecurityId(Aapl), new SecurityId(Msft)], priced);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>"Priced" needs BOTH bases (D30): a raw close to fill at and an adjusted close to score
    /// on. A bar with only one cannot complete the funnel, so the name is not eligible that day.</summary>
    [Fact]
    public void D78_PricedOn_ExcludesABarMissingEitherPriceBasis()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using (var seed = TestDb.Open(path))
            {
                // A third name whose asOf bar has a raw close but no adjusted close (e.g. an index).
                new SecurityMaster(seed).Register("NOADJ", "US", "2020-01-01"); // security_id 3
                new BarIngestionService(seed).IngestEod(
                    3, [Bar(AsOf, 50.0) with { AdjClose = null }], ObservedAtRun);
            }

            using var db = TestDb.Open(path);
            var view = View(db, WatermarkBefore);

            Assert.DoesNotContain(new SecurityId(3), view.PricedOn(AsOf));
            Assert.Equal(50.0, view.RawClose(new SecurityId(3), AsOf)); // the raw bar is really there
            Assert.Null(view.AdjClose(new SecurityId(3), AsOf));        // it just has no signal basis
        }
        finally { TestDb.Delete(path); }
    }

    // ============================ the ADV window (D43 inputs) ============================

    /// <summary>Rule 10: a partial window is null, never a shorter average. Averaging fewer days could
    /// read high or low with no way to tell which, and the number feeds the participation cap.</summary>
    [Fact]
    public void FR10_AnIncompleteAdvWindow_IsNull_NotAPartialAverage()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using (var seed = TestDb.Open(path))
            {
                // A name with only 5 sessions of history — well short of the 21-session ADV window.
                new SecurityMaster(seed).Register("NEW", "US", "2026-06-01"); // security_id 3
                var calendar = new CalendarService(seed);
                var recent = SessionsEndingAt(calendar, AsOf, 5);
                new BarIngestionService(seed).IngestEod(
                    3, recent.Select(d => Bar(d, 10.0)).ToList(), ObservedAtRun);
            }

            using var db = TestDb.Open(path);
            var view = View(db, WatermarkBefore);
            var thin = new SecurityId(3);

            Assert.Null(view.Adv21Shares(thin));
            Assert.Null(view.Adv21Notional(thin));
            Assert.Null(view.RealizedVolDaily(thin, 21));
            Assert.Equal(5, view.AdjCloseSeries(thin, 21).Count); // the series itself is honest about being short
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>The window is counted in CALENDAR sessions, not in available bars. A halted name must
    /// return a SHORT window — which becomes a null ADV, which becomes a refused order — rather than
    /// silently reaching back months to collect 21 values and calling that a 21-day average.</summary>
    [Fact]
    public void FR10_TheAdvWindowIsCountedInSessions_NotInAvailableBars()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using (var seed = TestDb.Open(path))
            {
                // A name that traded 30 sessions long ago and has been halted since: plenty of bars in
                // total, but almost none inside the last 21 sessions.
                new SecurityMaster(seed).Register("HALT", "US", "2020-01-01"); // security_id 3
                var calendar = new CalendarService(seed);
                var old = SessionsEndingAt(calendar, AsOf, 60).Take(30).ToList(); // sessions 60..31 back
                new BarIngestionService(seed).IngestEod(
                    3, old.Select(d => Bar(d, 10.0)).ToList(), ObservedAtRun);
            }

            using var db = TestDb.Open(path);
            var view = View(db, WatermarkBefore);
            var halted = new SecurityId(3);

            // 30 bars exist, but none of them are in the last 21 sessions.
            Assert.Null(view.Adv21Shares(halted));
            Assert.Empty(view.AdjCloseSeries(halted, 21));
            Assert.DoesNotContain(halted, view.PricedOn(AsOf));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR10_AdvShares_AveragesRawVolumeOverACompleteWindow()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using var db = TestDb.Open(path);

            var adv = View(db, WatermarkBefore).Adv21Shares(new SecurityId(Aapl));

            Assert.Equal(1_000_000.0, adv); // every seeded bar carries 1M shares
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>Notional is RAW close × raw volume — the money that actually changed hands. The adjusted
    /// close is a back-projected price that never existed; using it would understate historical notional
    /// by every dividend and split since, and could bucket a mega-cap as illiquid.</summary>
    [Fact]
    public void FR10_AdvNotional_UsesTheRawClose_NotTheAdjustedOne()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using (var seed = TestDb.Open(path))
            {
                // Raw 100, adjusted 50 — as if a 2:1 split had happened since. Notional must read 100 × vol.
                new SecurityMaster(seed).Register("SPLIT", "US", "2020-01-01"); // security_id 3
                var calendar = new CalendarService(seed);
                var history = SessionsEndingAt(calendar, AsOf, 30);
                new BarIngestionService(seed).IngestEod(
                    3,
                    history.Select(d => Bar(d, 100.0) with { AdjClose = 50.0 }).ToList(),
                    ObservedAtRun);
            }

            using var db = TestDb.Open(path);
            var view = View(db, WatermarkBefore);

            var notional = view.Adv21Notional(new SecurityId(3));

            Assert.Equal(100.0 * 1_000_000.0, notional);          // raw
            Assert.NotEqual(50.0 * 1_000_000.0, notional!.Value); // not adjusted
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>An N-session vol reads N+1 closes (PriceStatistics' convention) and matches the Core
    /// math exactly — the adapter must not reimplement it, or D50's regime vol and D43's σ drift apart.</summary>
    [Fact]
    public void FR10_RealizedVol_MatchesTheCoreMathOverTheSameWindow()
    {
        var path = SeededDb(withFutureBars: false);
        try
        {
            using var db = TestDb.Open(path);
            var view = View(db, WatermarkBefore);
            var id = new SecurityId(Aapl);

            var vol = view.RealizedVolDaily(id, 21);
            var expected = PriceStatistics.RealizedVolDaily(view.AdjCloseSeries(id, 22));

            Assert.NotNull(vol);
            Assert.Equal(expected, vol);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>The series ends AT asOf and is oldest-first — the order every windowed statistic
    /// downstream assumes. A reversed series would silently negate every return.</summary>
    [Fact]
    public void FR7_AdjCloseSeries_IsOldestFirst_AndEndsAtAsOf()
    {
        var path = SeededDb(withFutureBars: true);
        try
        {
            using var db = TestDb.Open(path);

            var series = View(db, WatermarkAfter).AdjCloseSeries(new SecurityId(Aapl), 5);

            Assert.Equal(5, series.Count);
            Assert.Equal([155.0, 156.0, 157.0, 158.0, 159.0], series); // ascending; 159 is the asOf close
        }
        finally { TestDb.Delete(path); }
    }
}
