using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-3 corporate-action feed: EODHD dividends + splits are ingested and typed into
/// corporate_actions (ingest+type only — processed_on NULL until Phase 2). End-to-end from a parsed
/// payload through the ingestion service, offline. Idempotency and the D69 decimal→TEXT round-trip
/// are asserted.
/// </summary>
public class CorporateActionIngestionTests
{
    private const long Sec = 1;

    private static string SeededDb()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new SecurityMaster(db).Register("ACME", "US", "2020-01-01"); // security_id = 1
        return path;
    }

    [Fact]
    public void FR3_IngestDividends_TypesRow_ExDate_UnadjustedCash_ProcessedOnNull()
    {
        var path = SeededDb();
        try
        {
            var divs = EodhdMarketDataProvider.ParseDividends("""
            [{"date":"2026-05-09","value":0.26,"unadjustedValue":0.26,"currency":"USD"}]
            """);

            using (var db = TestDb.Open(path))
            {
                var n = new CorporateActionIngestion(db).IngestDividends(Sec, divs, "2026-05-09T00:00:00Z");
                Assert.Equal(1, n);
            }
            using (var db = TestDb.Open(path))
            {
                var ca = Assert.Single(db.CorporateActions.Where(c => c.Type == "dividend").ToList());
                Assert.Equal(Sec, ca.SecurityId);
                Assert.Equal("2026-05-09", ca.ExDate);
                Assert.Equal("2026-05-09", ca.EffectiveDate);
                Assert.Equal(0.26m, ca.CashPerShare);
                Assert.Null(ca.Ratio);
                Assert.Null(ca.ProcessedOn);
                Assert.Equal("eodhd", ca.Source);
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR3_IngestSplits_TypesRow_ParsedRatio_ProcessedOnNull()
    {
        var path = SeededDb();
        try
        {
            var splits = EodhdMarketDataProvider.ParseSplits("""
            [{"date":"2020-08-31","split":"4.000000/1.000000"}]
            """);

            using (var db = TestDb.Open(path))
            {
                var n = new CorporateActionIngestion(db).IngestSplits(Sec, splits, "2020-08-31T00:00:00Z");
                Assert.Equal(1, n);
            }
            using (var db = TestDb.Open(path))
            {
                var ca = Assert.Single(db.CorporateActions.Where(c => c.Type == "split").ToList());
                Assert.Equal("2020-08-31", ca.EffectiveDate);
                Assert.Null(ca.ExDate);
                Assert.Equal(4.0, ca.Ratio);
                Assert.Null(ca.CashPerShare);
                Assert.Null(ca.ProcessedOn);
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR3_Ingestion_IsIdempotent_OnSecurityTypeEffectiveDate()
    {
        var path = SeededDb();
        try
        {
            var divs = EodhdMarketDataProvider.ParseDividends("""
            [{"date":"2026-05-09","value":0.26,"unadjustedValue":0.26}]
            """);
            var splits = EodhdMarketDataProvider.ParseSplits("""
            [{"date":"2020-08-31","split":"4.000000/1.000000"}]
            """);

            using (var db = TestDb.Open(path))
            {
                var svc = new CorporateActionIngestion(db);
                svc.IngestDividends(Sec, divs, "2026-05-09T00:00:00Z");
                svc.IngestSplits(Sec, splits, "2020-08-31T00:00:00Z");
            }
            using (var db = TestDb.Open(path))
            {
                var svc = new CorporateActionIngestion(db);
                Assert.Equal(0, svc.IngestDividends(Sec, divs, "2026-06-01T00:00:00Z")); // re-run: no dupes
                Assert.Equal(0, svc.IngestSplits(Sec, splits, "2020-09-15T00:00:00Z"));
            }
            using (var db = TestDb.Open(path))
            {
                Assert.Equal(2, db.CorporateActions.Count()); // exactly the one dividend + one split
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR3_CashPerShare_PersistsAsTextDecimal_D69()
    {
        var path = SeededDb();
        try
        {
            var divs = EodhdMarketDataProvider.ParseDividends("""
            [{"date":"2026-05-09","value":0.26,"unadjustedValue":0.255}]
            """);
            using (var db = TestDb.Open(path))
            {
                new CorporateActionIngestion(db).IngestDividends(Sec, divs, "2026-05-09T00:00:00Z");
            }

            // The column is TEXT (D69): the raw stored value is the decimal's string form, and it
            // round-trips back to the exact decimal (no float drift).
            using var db2 = TestDb.Open(path);
            var conn = db2.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT typeof(cash_per_share), cash_per_share FROM corporate_actions WHERE type='dividend';";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("text", r.GetString(0));
            Assert.Equal("0.255", r.GetString(1));
            Assert.Equal(0.255m, db2.CorporateActions.Single(c => c.Type == "dividend").CashPerShare);
        }
        finally { TestDb.Delete(path); }
    }
}
