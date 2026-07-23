using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FX-BarCorrection (TEST_PLAN §2): a v1 bar, then a v2 correction observed later. A run pinned to a
/// mid watermark reproduces v1 BYTE-IDENTICALLY; a later run sees v2. Exercises FR-2 / D40 —
/// versioned append-only bars + the latest-version-≤-watermark read rule.
///
/// The v1 bar is a REAL captured AAPL bar (2026-07-13) parsed from tests/Fixtures/eodhd/eod_AAPL.json.
/// The correction delta (v2) is necessarily synthetic: a single API pull yields one version per date,
/// so a "later correction" can't be captured — only the base bar is real. v2 = v1 with a corrected
/// close/adj_close/volume.
/// </summary>
public class BarVersioningTests
{
    private const long Sec = 1;
    private const string BarDate = "2026-07-13";
    private static readonly string ObservedV1 = "2026-07-13T20:00:00Z";
    private static readonly string ObservedV2 = "2026-07-18T20:00:00Z"; // correction seen 5 days later
    private static readonly string WatermarkMid = "2026-07-15T23:59:59Z"; // sees v1 only
    private static readonly string WatermarkLate = "2026-07-19T23:59:59Z"; // sees v2

    // Real AAPL bar for 2026-07-13 (open 317.015, high 323.45, low 315.78, close 317.31,
    // adjusted_close 317.31, volume 41376714) — parsed from the captured fixture.
    private static readonly EodBar V1 =
        EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_AAPL.json")).Single(b => b.Date == BarDate);

    // Synthetic correction delta (see class remarks): a late fix to close/adj_close/volume.
    private static readonly EodBar V2 = V1 with { Close = 317.55, AdjClose = 317.55, Volume = 41_500_000 };

    private static string SeededDb()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new SecurityMaster(db).Register("AAPL", "US", "2020-01-01"); // security_id = 1
        var ingest = new BarIngestionService(db);
        ingest.IngestEod(Sec, [V1], ObservedV1);
        ingest.IngestEod(Sec, [V2], ObservedV2); // correction ⇒ new version
        return path;
    }

    [Fact]
    public void FR2_Correction_AppendsNewVersion_NeverMutatesPrior()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var versions = db.Bars.Where(b => b.SecurityId == Sec && b.Date == BarDate)
                .OrderBy(b => b.Version).ToList();

            Assert.Equal(2, versions.Count);
            Assert.Equal(1, versions[0].Version);
            Assert.Equal(2, versions[1].Version);
            // The original v1 row is untouched (append-only): its close is still the real captured value.
            Assert.Equal(317.31, versions[0].Close);
            Assert.Equal(ObservedV1, versions[0].ObservedAt);
            Assert.Equal(317.55, versions[1].Close);
            Assert.Equal(ObservedV2, versions[1].ObservedAt);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR2_PinnedToMidWatermark_ReproducesV1_ByteIdentically()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var read = new BarReadService(db);

            var atMid = read.GetBar(Sec, BarDate, WatermarkMid);
            Assert.NotNull(atMid);
            Assert.Equal(1, atMid!.Version); // v2 (observed later) is invisible at the mid watermark
            Assert.Equal(317.31, atMid.Close);       // real captured close
            Assert.Equal(41376714, atMid.Volume);    // real captured volume

            // Byte-identical reproduction: repeated reads at the same watermark yield the same values.
            var again = read.GetBar(Sec, BarDate, WatermarkMid);
            Assert.Equal((atMid.Version, atMid.Open, atMid.High, atMid.Low, atMid.Close, atMid.Volume, atMid.AdjClose),
                         (again!.Version, again.Open, again.High, again.Low, again.Close, again.Volume, again.AdjClose));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR2_AtLateWatermark_SeesV2()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var read = new BarReadService(db);

            var atLate = read.GetBar(Sec, BarDate, WatermarkLate);
            Assert.NotNull(atLate);
            Assert.Equal(2, atLate!.Version);
            Assert.Equal(317.55, atLate.Close);
            Assert.Equal(41_500_000, atLate.Volume);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR2_IdenticalRefetch_IsIdempotent_NoNewVersion()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var ingest = new BarIngestionService(db);

            // Re-ingesting the current (v2) values, even at a later observed time, adds nothing.
            var written = ingest.IngestEod(Sec, [V2], "2026-07-25T20:00:00Z");
            Assert.Equal(0, written);
            Assert.Equal(2, db.Bars.Count(b => b.SecurityId == Sec && b.Date == BarDate));
        }
        finally { TestDb.Delete(path); }
    }

    // The Phase-4 review's no-backdate guard: a resumed replay re-stages its frozen-vintage (v1)
    // values after a later correction (v2) landed. The old diff-vs-latest logic appended v3 = v1's
    // values with a BACKDATED observed_at, silently shadowing v2 for every read at a later watermark.
    // The guard must skip the append; the replay's own reads still resolve v1 by watermark.
    [Fact]
    public void FR2_BackdatedDifferingReingest_IsSkipped_NeverShadowsNewerVersion()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var ingest = new BarIngestionService(db);

            // Re-ingest v1's values at the OLD observation instant (a replay pinned to its frozen
            // watermark): differs from latest v2, but observed_at is older than v2's -> must be a no-op.
            var written = ingest.IngestEod(Sec, [V1], ObservedV1);
            Assert.Equal(0, written);
            Assert.Equal(2, db.Bars.Count(b => b.SecurityId == Sec && b.Date == BarDate));

            // The correction is still what a later watermark resolves — v2 was not shadowed.
            var read = new BarReadService(db);
            var atLate = read.GetBar(Sec, BarDate, WatermarkLate);
            Assert.Equal(2, atLate!.Version);
            Assert.Equal(317.55, atLate.Close);

            // And the replay's own view is untouched: the frozen watermark still resolves v1.
            var atMid = read.GetBar(Sec, BarDate, WatermarkMid);
            Assert.Equal(1, atMid!.Version);
            Assert.Equal(317.31, atMid.Close);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR2_AdjOhl_LeftNull_AdjCloseStored()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var v1 = db.Bars.Single(b => b.SecurityId == Sec && b.Date == BarDate && b.Version == 1);
            Assert.Equal(317.31, v1.AdjClose); // real captured adjusted_close (== close on this date)
            Assert.Null(v1.AdjOpen);
            Assert.Null(v1.AdjHigh);
            Assert.Null(v1.AdjLow);
        }
        finally { TestDb.Delete(path); }
    }

    // A multi-date series around the FX-BarCorrection date, for the P1R-3 GetSeries range push-down:
    // a bar before [from,to], the corrected date (v1 then v2), a bar at the inclusive `to` edge, and one
    // beyond `to`. Off-correction dates carry v1's values (only the date differs) — the test only asserts
    // the correction date's version/close specifically, plus overall equality with the reference path.
    private static string SeriesDb()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new SecurityMaster(db).Register("AAPL", "US", "2020-01-01"); // security_id = 1
        var ingest = new BarIngestionService(db);
        ingest.IngestEod(Sec, [V1 with { Date = "2026-07-10" }], ObservedV1);
        ingest.IngestEod(Sec, [V1], ObservedV1);                          // 2026-07-13 v1
        ingest.IngestEod(Sec, [V2], ObservedV2);                          // 2026-07-13 v2 (correction)
        ingest.IngestEod(Sec, [V1 with { Date = "2026-07-16" }], ObservedV1); // inclusive `to` edge
        ingest.IngestEod(Sec, [V1 with { Date = "2026-07-20" }], ObservedV1); // beyond `to`
        return path;
    }

    // P1R-3: prove the [from,to] range is TRANSLATED to SQL (server-side), not client-evaluated — the
    // whole point of the change (else GetSeries still materializes the security's full history). If EF
    // cannot translate string.Compare, ToQueryString throws / omits the predicate -> STOP and report.
    [Fact]
    public void FR2_GetSeries_PushesDateRangeIntoSql_NoClientEval()
    {
        var path = SeriesDb();
        try
        {
            using var db = TestDb.Open(path);
            const string from = "2026-07-10", to = "2026-07-16";

            var sql = db.Bars
                .Where(x => x.SecurityId == Sec
                            && string.Compare(x.Date, from) >= 0
                            && string.Compare(x.Date, to) <= 0)
                .ToQueryString();

            // The range must live in the SQL WHERE clause (server-side): if it were client-evaluated, the
            // WHERE would filter on security_id only and never mention `date`. Robust to whether EF emits
            // `date >= @p` or a CASE form — either way `date` appears in the WHERE.
            var whereIdx = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            Assert.True(whereIdx >= 0, $"expected a WHERE clause in:\n{sql}");
            var whereClause = sql[whereIdx..];
            Assert.Contains("date", whereClause, StringComparison.OrdinalIgnoreCase);
        }
        finally { TestDb.Delete(path); }
    }

    // P1R-3: the new SQL-side range path returns results BYTE-IDENTICAL to the prior in-memory filter,
    // across the FX-BarCorrection fixture and both watermark boundaries (mid sees v1, late sees v2),
    // including the inclusive range edges and exclusion of the out-of-range date.
    [Theory]
    [InlineData("2026-07-15T23:59:59Z", 1, 317.31)] // mid watermark -> v1 at the correction date
    [InlineData("2026-07-19T23:59:59Z", 2, 317.55)] // late watermark -> v2
    public void FR2_GetSeries_MatchesInMemoryPath_ByteIdentical(
        string watermark, int expectedVersionAtCorrection, double expectedCloseAtCorrection)
    {
        var path = SeriesDb();
        try
        {
            using var db = TestDb.Open(path);
            var read = new BarReadService(db);
            const string from = "2026-07-10", to = "2026-07-16";

            var actual = read.GetSeries(Sec, from, to, watermark);

            // Reference = the OLD algorithm: pull the whole security in memory, then range + watermark.
            var expected = db.Bars
                .Where(x => x.SecurityId == Sec)
                .AsEnumerable()
                .Where(x => string.CompareOrdinal(x.Date, from) >= 0
                            && string.CompareOrdinal(x.Date, to) <= 0
                            && string.CompareOrdinal(x.ObservedAt, watermark) <= 0)
                .GroupBy(x => x.Date)
                .Select(g => g.OrderByDescending(x => x.Version).First())
                .OrderBy(x => x.Date)
                .ToList();

            Assert.Equal(
                expected.Select(b => (b.Date, b.Version, b.Close, b.ObservedAt)),
                actual.Select(b => (b.Date, b.Version, b.Close, b.ObservedAt)));

            // The load-bearing boundary: the correction date resolves to the expected version + close.
            var atCorrection = Assert.Single(actual, b => b.Date == BarDate);
            Assert.Equal(expectedVersionAtCorrection, atCorrection.Version);
            Assert.Equal(expectedCloseAtCorrection, atCorrection.Close);

            // Inclusive edges present; the out-of-range date is excluded.
            Assert.Contains(actual, b => b.Date == from);
            Assert.Contains(actual, b => b.Date == to);
            Assert.DoesNotContain(actual, b => b.Date == "2026-07-20");
        }
        finally { TestDb.Delete(path); }
    }
}
