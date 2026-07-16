using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// D76 / rule-4 (F-LEAK): corporate_actions is versioned append-only like bars. A dividend, then a
/// later-observed RESTATEMENT of its cash, appends a v2 — never an UPDATE. A run pinned to a mid
/// watermark reproduces v1 BYTE-IDENTICALLY (the restatement is invisible); a later run sees v2. And a
/// genuinely NEW action observed after a watermark is invisible at that watermark — the exact replay
/// future-leak this decision closes (a replay pinned to 2015 must not price a 2026-observed action).
///
/// The correction delta is synthetic by necessity: one API pull yields one version per (type, date), so
/// a "later restatement" cannot be captured — only the base event is real-shaped.
/// </summary>
public class CorporateActionVersioningTests
{
    private const long Sec = 1;
    private const string ExDate = "2024-05-10";              // the dividend's ex-date / effective_date
    private static readonly string ObservedV1 = "2024-05-11T00:00:00Z";
    private static readonly string ObservedV2 = "2024-06-01T00:00:00Z"; // restatement seen ~3 weeks later
    private static readonly string WatermarkMid = "2024-05-20T00:00:00Z"; // sees v1 only
    private static readonly string WatermarkLate = "2024-06-15T00:00:00Z"; // sees v2

    // v1 = 0.25 actual cash; v2 = a restated 0.26 (same ex-date ⇒ a correction, not a new action).
    private static readonly DividendEvent V1 = new(ExDate, 0.25m, 0.25m);
    private static readonly DividendEvent V2 = new(ExDate, 0.26m, 0.26m);

    private static string SeededDb()
    {
        var path = TestDb.CreateMigrated();
        using var db = TestDb.Open(path);
        new SecurityMaster(db).Register("AAPL", "US", "2020-01-01"); // security_id = 1
        var ingest = new CorporateActionIngestion(db);
        ingest.IngestDividends(Sec, [V1], ObservedV1);
        ingest.IngestDividends(Sec, [V2], ObservedV2); // restatement ⇒ new version
        return path;
    }

    [Fact]
    public void D76_Restatement_AppendsNewVersion_NeverMutatesPrior()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var versions = db.CorporateActions
                .Where(c => c.SecurityId == Sec && c.Type == "dividend" && c.EffectiveDate == ExDate)
                .OrderBy(c => c.Version).ToList();

            Assert.Equal(2, versions.Count);
            Assert.Equal(1, versions[0].Version);
            Assert.Equal(2, versions[1].Version);
            // The original v1 row is untouched (append-only): its cash is still the first value.
            Assert.Equal(0.25m, versions[0].CashPerShare);
            Assert.Equal(ObservedV1, versions[0].ObservedAt);
            Assert.Equal(0.26m, versions[1].CashPerShare);
            Assert.Equal(ObservedV2, versions[1].ObservedAt);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void D76_PinnedToMidWatermark_ReproducesV1_ByteIdentically()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var read = new CorporateActionReadService(db);

            var atMid = read.GetActionsAsOf(Sec, WatermarkMid);
            var div = Assert.Single(atMid); // one (type, effective_date) identity
            Assert.Equal(1, div.Version);   // v2 (observed later) is invisible at the mid watermark
            Assert.Equal(0.25m, div.CashPerShare);
            Assert.Equal(ObservedV1, div.ObservedAt);

            // Byte-identical reproduction: repeated reads at the same watermark yield the same values.
            var again = Assert.Single(read.GetActionsAsOf(Sec, WatermarkMid));
            Assert.Equal((div.Version, div.CashPerShare, div.ObservedAt),
                         (again.Version, again.CashPerShare, again.ObservedAt));
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void D76_AtLateWatermark_SeesV2()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var atLate = new CorporateActionReadService(db).GetActionsAsOf(Sec, WatermarkLate);
            var div = Assert.Single(atLate);
            Assert.Equal(2, div.Version);
            Assert.Equal(0.26m, div.CashPerShare);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void D76_IdenticalRefetch_IsIdempotent_NoNewVersion()
    {
        var path = SeededDb();
        try
        {
            using var db = TestDb.Open(path);
            var ingest = new CorporateActionIngestion(db);

            // Re-ingesting the current (v2) value, even at a later observed time, adds nothing.
            var written = ingest.IngestDividends(Sec, [V2], "2024-07-01T00:00:00Z");
            Assert.Equal(0, written);
            Assert.Equal(2, db.CorporateActions.Count(c => c.SecurityId == Sec && c.EffectiveDate == ExDate));
        }
        finally { TestDb.Delete(path); }
    }

    // The core replay-leak scenario: a genuinely NEW action (a later ex-date dividend) observed AFTER a
    // watermark must be invisible at that watermark — else a replay pinned to the past would price a
    // future action (breaking NFR1 determinism, the property D76 buys for the ledger's dividend feed).
    [Fact]
    public void D76_FutureObservedAction_IsInvisibleAtEarlierWatermark()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            new SecurityMaster(db).Register("AAPL", "US", "2020-01-01");
            var ingest = new CorporateActionIngestion(db);
            ingest.IngestDividends(Sec, [new DividendEvent(ExDate, 0.25m, 0.25m)], ObservedV1);         // May
            ingest.IngestDividends(Sec, [new DividendEvent("2024-08-09", 0.27m, 0.27m)], ObservedV2);   // Aug, observed later

            var read = new CorporateActionReadService(db);

            // At the mid watermark (before the August action was observed) only the May dividend is visible.
            var atMid = read.GetActionsAsOf(Sec, WatermarkMid);
            var only = Assert.Single(atMid);
            Assert.Equal(ExDate, only.EffectiveDate);

            // At the late watermark both are visible, ordered by effective_date.
            var atLate = read.GetActionsAsOf(Sec, WatermarkLate);
            Assert.Equal(2, atLate.Count);
            Assert.Equal([ExDate, "2024-08-09"], atLate.Select(c => c.EffectiveDate));
        }
        finally { TestDb.Delete(path); }
    }
}
