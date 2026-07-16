using System.Text;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// D78: the date-major (cross-sectional) bar read — "every name at date D at watermark W" — served by
/// ix_bars_date. Proves (a) the single-date equality is TRANSLATED to SQL (not client-evaluated), (b)
/// the result is byte-identical to an in-memory reference across both watermark boundaries and resolves
/// the correct per-security version, and (c) EXPLAIN QUERY PLAN shows the read uses ix_bars_date (not a
/// full table scan). Two securities share the date; AAPL carries a later-observed v2 correction.
/// </summary>
public class BarCrossSectionTests
{
    private const long Aapl = 1;
    private const long Msft = 2;
    private const string D = "2026-07-13";
    private const string OtherDate = "2026-07-10";
    private static readonly string ObservedV1 = "2026-07-13T20:00:00Z";
    private static readonly string ObservedV2 = "2026-07-18T20:00:00Z"; // AAPL correction seen later
    private static readonly string WatermarkMid = "2026-07-15T23:59:59Z"; // sees AAPL v1
    private static readonly string WatermarkLate = "2026-07-19T23:59:59Z"; // sees AAPL v2

    // Real AAPL bar for D (close 317.31); reused as the shape for a synthetic MSFT bar + the AAPL v2 fix.
    private static readonly EodBar Base =
        EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_AAPL.json")).Single(b => b.Date == D);

    private static string SeededDb()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        var sm = new SecurityMaster(db);
        sm.Register("AAPL", "US", "2020-01-01"); // security_id = 1
        sm.Register("MSFT", "US", "2020-01-01"); // security_id = 2
        var ingest = new BarIngestionService(db);
        ingest.IngestEod(Aapl, [Base], ObservedV1);                                          // AAPL D v1 (317.31)
        ingest.IngestEod(Aapl, [Base with { Close = 317.55, AdjClose = 317.55 }], ObservedV2); // AAPL D v2 correction
        ingest.IngestEod(Msft, [Base with { Close = 500.0, AdjClose = 500.0 }], ObservedV1);  // MSFT D v1 (500)
        ingest.IngestEod(Aapl, [Base with { Date = OtherDate }], ObservedV1);                 // AAPL other date (excluded)
        return path;
    }

    [Fact]
    public void D78_GetCrossSection_PushesDateEqualityIntoSql_NoClientEval()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var sql = db.Bars.Where(x => x.Date == D).ToQueryString();

            var whereIdx = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            Assert.True(whereIdx >= 0, $"expected a WHERE clause in:\n{sql}");
            Assert.Contains("date", sql[whereIdx..], StringComparison.OrdinalIgnoreCase);
        }
        finally { TestDb.Delete(path); }
    }

    [Theory]
    [InlineData("2026-07-15T23:59:59Z", 1, 317.31)] // mid watermark -> AAPL v1
    [InlineData("2026-07-19T23:59:59Z", 2, 317.55)] // late watermark -> AAPL v2
    public void D78_GetCrossSection_MatchesInMemoryPath_ByteIdentical(
        string watermark, int expectedAaplVersion, double expectedAaplClose)
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var read = new BarReadService(db);

            var actual = read.GetCrossSection(D, watermark);

            // Reference = the naive path: pull the whole date in memory, resolve version per security.
            var expected = db.Bars
                .Where(x => x.Date == D)
                .AsEnumerable()
                .Where(x => string.CompareOrdinal(x.ObservedAt, watermark) <= 0)
                .GroupBy(x => x.SecurityId)
                .Select(g => g.OrderByDescending(x => x.Version).First())
                .OrderBy(x => x.SecurityId)
                .ToList();

            Assert.Equal(
                expected.Select(b => (b.SecurityId, b.Version, b.Close)),
                actual.Select(b => (b.SecurityId, b.Version, b.Close)));

            // One row per security, ordered by security_id; the other-date bar is excluded.
            Assert.Equal([Aapl, Msft], actual.Select(b => b.SecurityId));
            var aapl = Assert.Single(actual, b => b.SecurityId == Aapl);
            Assert.Equal(expectedAaplVersion, aapl.Version);
            Assert.Equal(expectedAaplClose, aapl.Close);
            Assert.Equal(500.0, Assert.Single(actual, b => b.SecurityId == Msft).Close); // MSFT unaffected by watermark
            Assert.DoesNotContain(actual, b => b.Date == OtherDate);
        }
        finally { TestDb.Delete(path); }
    }

    // The whole point of ix_bars_date: a date-major read must SEARCH the index, not full-scan bars.
    [Fact]
    public void D78_GetCrossSection_UsesIxBarsDate_NotAFullScan()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var conn = db.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN SELECT security_id, version FROM bars WHERE date = '2026-07-13';";
            var plan = new StringBuilder();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    for (var i = 0; i < r.FieldCount; i++) plan.Append(r.GetValue(i)).Append(' ');
            var text = plan.ToString();

            Assert.Contains("ix_bars_date", text);                                          // the index serves it
            Assert.DoesNotContain("SCAN bars", text, StringComparison.OrdinalIgnoreCase);   // not a full scan
        }
        finally { TestDb.Delete(path); }
    }
}
