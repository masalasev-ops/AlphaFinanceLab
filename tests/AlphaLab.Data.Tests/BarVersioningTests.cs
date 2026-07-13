using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FX-BarCorrection (TEST_PLAN §2): v1 bar on day 10; a v2 correction is observed on day 15. A run
/// pinned to a day-12 watermark reproduces the day-10 bar BYTE-IDENTICALLY (v1); a day-16 run sees
/// v2. Exercises FR-2 / D40 — versioned append-only bars + the latest-version-≤-watermark read rule.
/// </summary>
public class BarVersioningTests
{
    private const long Sec = 1;
    private const string Day10 = "2026-01-10";
    private static readonly string ObservedV1 = "2026-01-10T20:00:00Z";
    private static readonly string ObservedV2 = "2026-01-15T20:00:00Z";
    private static readonly string WatermarkDay12 = "2026-01-12T23:59:59Z";
    private static readonly string WatermarkDay16 = "2026-01-16T23:59:59Z";

    // v1 (as first published) and v2 (the correction) for the same date.
    private static readonly EodBar V1 = new(Day10, 100.0, 101.0, 99.0, 100.5, 100.5, 1_000_000);
    private static readonly EodBar V2 = new(Day10, 100.0, 101.0, 99.0, 100.9, 100.9, 1_050_000);

    private static string SeededDb()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new SecurityMaster(db).Register("ACME", "US", "2026-01-01"); // security_id = 1
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
            var versions = db.Bars.Where(b => b.SecurityId == Sec && b.Date == Day10)
                .OrderBy(b => b.Version).ToList();

            Assert.Equal(2, versions.Count);
            Assert.Equal(1, versions[0].Version);
            Assert.Equal(2, versions[1].Version);
            // The original v1 row is untouched (append-only): its close is still the published value.
            Assert.Equal(100.5, versions[0].Close);
            Assert.Equal(ObservedV1, versions[0].ObservedAt);
            Assert.Equal(100.9, versions[1].Close);
            Assert.Equal(ObservedV2, versions[1].ObservedAt);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR2_PinnedToDay12Watermark_ReproducesV1_ByteIdentically()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var read = new BarReadService(db);

            var atDay12 = read.GetBar(Sec, Day10, WatermarkDay12);
            Assert.NotNull(atDay12);
            Assert.Equal(1, atDay12!.Version); // v2 (observed day 15) is invisible at a day-12 watermark
            Assert.Equal(100.5, atDay12.Close);
            Assert.Equal(1_000_000, atDay12.Volume);

            // Byte-identical reproduction: repeated reads at the same watermark yield the same values.
            var again = read.GetBar(Sec, Day10, WatermarkDay12);
            Assert.Equal((atDay12.Version, atDay12.Open, atDay12.High, atDay12.Low, atDay12.Close, atDay12.Volume, atDay12.AdjClose),
                         (again!.Version, again.Open, again.High, again.Low, again.Close, again.Volume, again.AdjClose));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void FR2_AtDay16Watermark_SeesV2()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var read = new BarReadService(db);

            var atDay16 = read.GetBar(Sec, Day10, WatermarkDay16);
            Assert.NotNull(atDay16);
            Assert.Equal(2, atDay16!.Version);
            Assert.Equal(100.9, atDay16.Close);
            Assert.Equal(1_050_000, atDay16.Volume);
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
            var written = ingest.IngestEod(Sec, [V2], "2026-01-20T20:00:00Z");
            Assert.Equal(0, written);
            Assert.Equal(2, db.Bars.Count(b => b.SecurityId == Sec && b.Date == Day10));
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
            var v1 = db.Bars.Single(b => b.SecurityId == Sec && b.Date == Day10 && b.Version == 1);
            Assert.Equal(100.5, v1.AdjClose);
            Assert.Null(v1.AdjOpen);
            Assert.Null(v1.AdjHigh);
            Assert.Null(v1.AdjLow);
        }
        finally { TestDb.Delete(path); }
    }
}
