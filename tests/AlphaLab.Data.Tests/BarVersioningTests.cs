using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

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
}
