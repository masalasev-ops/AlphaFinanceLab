using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-3 corporate-action feed: REAL EODHD dividends + splits (tests/Fixtures/eodhd/div_AAPL.json,
/// splits_AAPL.json) are ingested and typed into corporate_actions (ingest+type only; the ledger
/// applies actions via one-transaction-per-day, never a per-action flag — D94/M5 dropped the
/// always-NULL processed_on column). End-to-end from the parsers (1.2) through the ingestion
/// service, offline. Idempotency and the D69 decimal→TEXT round-trip are asserted against real values.
/// </summary>
public class CorporateActionIngestionTests
{
    private const long Sec = 1;

    private static string SeededDb()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new SecurityMaster(db).Register("AAPL", "US", "1980-01-01"); // security_id = 1
        return path;
    }

    private static IReadOnlyList<DividendEvent> RealDividends() =>
        EodhdMarketDataProvider.ParseDividends("AAPL", Fixtures.Eodhd("div_AAPL.json"));

    private static IReadOnlyList<SplitEvent> RealSplits() =>
        EodhdMarketDataProvider.ParseSplits(Fixtures.Eodhd("splits_AAPL.json"));

    [Fact]
    public void FR3_IngestRealDividends_TypesRows_UnadjustedCash_ExDate()
    {
        var path = SeededDb();
        try
        {
            using (var db = TestDb.Open(path))
            {
                var n = new CorporateActionIngestion(db).IngestDividends(Sec, RealDividends(), "2026-07-13T00:00:00Z");
                Assert.Equal(80, n);
            }
            using (var db = TestDb.Open(path))
            {
                var divs = db.CorporateActions.Where(c => c.Type == "dividend").ToList();
                Assert.Equal(80, divs.Count);
                Assert.All(divs, c => Assert.Null(c.Ratio));

                // Recent (2026-05-11): unadjusted == adjusted == 0.27.
                var recent = Assert.Single(divs, c => c.EffectiveDate == "2026-05-11");
                Assert.Equal("2026-05-11", recent.ExDate);
                Assert.Equal(0.27m, recent.CashPerShare);

                // Oldest (1990-02-16): cash_per_share is the UNADJUSTED actual cash (0.10976), NOT the
                // retro split-adjusted value (0.00098).
                var old = Assert.Single(divs, c => c.EffectiveDate == "1990-02-16");
                Assert.Equal(0.10976m, old.CashPerShare);
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR3_IngestRealSplits_TypesRows_ParsedRatio()
    {
        var path = SeededDb();
        try
        {
            using (var db = TestDb.Open(path))
            {
                var n = new CorporateActionIngestion(db).IngestSplits(Sec, RealSplits(), "2026-07-13T00:00:00Z");
                Assert.Equal(5, n);
            }
            using (var db = TestDb.Open(path))
            {
                var splits = db.CorporateActions.Where(c => c.Type == "split").ToList();
                Assert.Equal(5, splits.Count);
                Assert.All(splits, c => Assert.Null(c.ExDate));
                Assert.All(splits, c => Assert.Null(c.CashPerShare));

                var fourForOne = Assert.Single(splits, c => c.EffectiveDate == "2020-08-31");
                Assert.Equal(4.0, fourForOne.Ratio);
            }
        }
        finally { TestDb.Delete(path); }
    }

    // D76: ingestion is now VALUE-DIFF append-only (a changed value for the same (security, type,
    // effective_date) would append a new version — see CorporateActionVersioningTests). An IDENTICAL
    // re-fetch, even at a later observed time, stays a no-op — so this idempotency assertion holds
    // unchanged (0 new on re-run, total 85).
    [Fact]
    public void FR3_Ingestion_IsIdempotent_OnSecurityTypeEffectiveDate()
    {
        var path = SeededDb();
        try
        {
            using (var db = TestDb.Open(path))
            {
                var svc = new CorporateActionIngestion(db);
                svc.IngestDividends(Sec, RealDividends(), "2026-07-13T00:00:00Z");
                svc.IngestSplits(Sec, RealSplits(), "2026-07-13T00:00:00Z");
            }
            using (var db = TestDb.Open(path))
            {
                var svc = new CorporateActionIngestion(db);
                Assert.Equal(0, svc.IngestDividends(Sec, RealDividends(), "2026-08-01T00:00:00Z")); // re-run: no dupes
                Assert.Equal(0, svc.IngestSplits(Sec, RealSplits(), "2026-08-01T00:00:00Z"));
            }
            using (var db = TestDb.Open(path))
            {
                Assert.Equal(85, db.CorporateActions.Count()); // 80 dividends + 5 splits, no duplicates
            }
        }
        finally { TestDb.Delete(path); }
    }

    // Defensive secondary guard (P1R-1): the EODHD parse boundary already rejects a null
    // unadjustedValue, so this covers a directly-constructed DividendEvent (a non-provider path) —
    // ingestion refuses to write split-adjusted cash and names the security + ex-date.
    [Fact]
    public void FR3_IngestDividends_NullUnadjustedValue_FailsClosed_NamingSecurityAndExDate()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var bad = new[] { new DividendEvent("2020-01-01", 0.25m, null) };

            var ex = Assert.Throws<InvalidOperationException>(
                () => new CorporateActionIngestion(db).IngestDividends(Sec, bad, "2026-07-13T00:00:00Z"));
            Assert.Contains("security_id=1", ex.Message);
            Assert.Contains("2020-01-01", ex.Message);
            Assert.Empty(db.CorporateActions.ToList()); // nothing written
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR3_CashPerShare_PersistsAsTextDecimal_D69()
    {
        var path = SeededDb();
        try
        {
            using (var db = TestDb.Open(path))
            {
                new CorporateActionIngestion(db).IngestDividends(Sec, RealDividends(), "2026-07-13T00:00:00Z");
            }

            // The column is TEXT (D69): the raw stored value is the decimal's string form, and it
            // round-trips back to the exact decimal (no float drift).
            using var db2 = TestDb.Open(path);
            var conn = db2.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT typeof(cash_per_share), cash_per_share FROM corporate_actions WHERE type='dividend' AND effective_date='2026-05-11';";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("text", r.GetString(0));
            Assert.Equal("0.27", r.GetString(1));
            Assert.Equal(0.27m, db2.CorporateActions.Single(c => c.Type == "dividend" && c.EffectiveDate == "2026-05-11").CashPerShare);
        }
        finally { TestDb.Delete(path); }
    }
}
