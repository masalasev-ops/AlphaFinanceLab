using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Finding 193 (Phase-4 prerequisite): the incremental-fetch cursor is ONE MAX(date) query, never the
/// old GetSeries("0001-01-01", …) full-history materialization — per security per day that scan is what
/// the sp500 widen and multi-day catch-up cannot afford. Behavior must be identical to the old path:
/// the latest stored date ≤ upTo with any version visible at the watermark.
/// </summary>
public class BarLastStoredDateTests
{
    private const long Aapl = 1;
    private const string EarlyWatermark = "2026-07-03T22:00:00Z";
    private const string LateWatermark = "2026-07-06T22:00:00Z";
    private const string ObservedEarly = "2026-07-03T20:00:00Z"; // visible at both watermarks
    private const string ObservedLate = "2026-07-06T20:00:00Z";  // visible only at the late watermark

    private static void Seed(AlphaLabDbContext db)
    {
        new SecurityMaster(db).Register("AAPL", "US", "2020-01-01"); // security_id 1
        var ingest = new BarIngestionService(db);
        ingest.IngestEod(Aapl,
        [
            new EodBar("2026-07-01", 100, 101, 99, 100, 100, 1_000_000),
            new EodBar("2026-07-02", 100, 102, 99, 101, 101, 1_000_000),
            new EodBar("2026-07-03", 101, 103, 100, 102, 102, 1_000_000),
        ], ObservedEarly);
        // A LATER session's bar, observed after the early watermark…
        ingest.IngestEod(Aapl, [new EodBar("2026-07-06", 102, 104, 101, 103, 103, 1_000_000)], ObservedLate);
        // …and a late-observed CORRECTION (version 2) of an early date: the date itself stays visible
        // at the early watermark through its v1, so any-visible-version-proves-the-date must hold.
        ingest.IngestEod(Aapl, [new EodBar("2026-07-03", 101, 103, 100, 102.5, 102.5, 1_000_000)], ObservedLate);
    }

    /// <summary>The old path, verbatim (DailyPipeline pre-Phase-4): full-series read, last element.</summary>
    private static string? OldPath(IBarReadService bars, long id, string upTo, string watermark)
    {
        var series = bars.GetSeries(id, "0001-01-01", upTo, watermark);
        return series.Count == 0 ? null : series[^1].Date;
    }

    [Fact]
    public void FR19_LastStoredDate_MaxDateQuery_MatchesOldPath()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            Seed(db);
            var bars = new BarReadService(db);

            // Every (upTo, watermark) shape the pipeline uses, against the old path byte-for-byte.
            foreach (var (upTo, watermark) in new[]
                     {
                         ("2026-07-31", EarlyWatermark), // late bar invisible → 07-03
                         ("2026-07-31", LateWatermark),  // late bar visible   → 07-06
                         ("2026-07-02", LateWatermark),  // upTo bound         → 07-02
                         ("2026-06-30", LateWatermark),  // nothing that early → null
                     })
            {
                Assert.Equal(OldPath(bars, Aapl, upTo, watermark), bars.LastStoredDate(Aapl, upTo, watermark));
            }

            Assert.Equal("2026-07-03", bars.LastStoredDate(Aapl, "2026-07-31", EarlyWatermark));
            Assert.Equal("2026-07-06", bars.LastStoredDate(Aapl, "2026-07-31", LateWatermark));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR19_LastStoredDate_UnknownSecurity_IsNull()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            Seed(db);
            Assert.Null(new BarReadService(db).LastStoredDate(999, "2026-07-31", LateWatermark));
        }
        finally { TestDb.Delete(path); }
    }
}
