using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FX-SectorReclass (TEST_PLAN §2) — FR-5 sector ingestion + change log. A mid-period reclassification
/// writes a sector_changes row and updates the current value; an initial classification sets a baseline
/// without a change row; an unchanged apply is a no-op. Industry stays null at launch (the IVV/OEF CSV
/// has no industry column; the EODHD-fundamentals source is dormant, D49).
/// </summary>
public class SectorIngestionTests
{
    [Fact]
    public void FX_SectorReclass_MidPeriodChange_LoggedAndCurrentUpdated()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            long id;
            using (var db = TestDb.Open(path))
            {
                id = new SecurityMaster(db).Register("XYZ", "US", "2026-01-01", sector: "Information Technology");
                var n = new SectorIngestion(db).ApplySectors(
                    [new SectorAssignment(id, "Communication Services")], "2026-02-01");
                Assert.Equal(1, n);
            }
            using (var db = TestDb.Open(path))
            {
                Assert.Equal("Communication Services", db.Securities.Find(id)!.Sector);
                var chg = Assert.Single(db.SectorChanges.Where(s => s.SecurityId == id).ToList());
                Assert.Equal(("Information Technology", "Communication Services", "2026-02-01"),
                    (chg.OldSector, chg.NewSector, chg.ChangedOn));
                Assert.Null(chg.OldIndustry);
                Assert.Null(chg.NewIndustry);
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void ApplySectors_Unchanged_IsNoOp()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = new SecurityMaster(db).Register("XYZ", "US", "2026-01-01", sector: "Health Care");
            var ing = new SectorIngestion(db);
            Assert.Equal(0, ing.ApplySectors([new SectorAssignment(id, "Health Care")], "2026-02-01"));
            Assert.Empty(db.SectorChanges.ToList());
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void ApplySectors_InitialClassification_SetsBaseline_NoChangeRow()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = new SecurityMaster(db).Register("XYZ", "US", "2026-01-01"); // sector null
            var n = new SectorIngestion(db).ApplySectors([new SectorAssignment(id, "Financials")], "2026-02-01");
            Assert.Equal(0, n);
            Assert.Empty(db.SectorChanges.ToList());
            Assert.Equal("Financials", db.Securities.Find(id)!.Sector);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void ApplySectors_FromRealIvvCsv_SetsGicsSector_IndustryStaysNull()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            var nvda = ISharesHoldingsParser.ParseHoldings(Fixtures.Holdings("IVV_holdings.csv"))
                .Single(h => h.Ticker == "NVDA");
            using var db = TestDb.Open(path);
            var id = new SecurityMaster(db).Register("NVDA", "US", "2026-01-01");
            new SectorIngestion(db).ApplySectors([new SectorAssignment(id, nvda.Sector)], "2026-01-01");

            var sec = db.Securities.Find(id)!;
            Assert.Equal("Information Technology", sec.Sector);
            Assert.Null(sec.Industry); // the IVV CSV has no industry column
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void DormantEodhdSectorProvider_FailsLoud()
    {
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = new EodhdFundamentalsSectorProvider().GetSectorAsync("AAPL");
        });
    }
}
