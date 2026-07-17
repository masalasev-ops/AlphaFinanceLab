using AlphaLab.Core.Regime;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-26/D50 regime-label service (§20.1). Covers the fail-closed guards (unresolved proxy, below warm-up,
/// no bar on asOf), proxy-id resolution from the VERSIONED config row (MAX(version)), F-LEAK (the label at
/// asOf is invariant to future bars and later-observed corrections; the watermark is IN the provenance),
/// and the D45 episode chain (a confirmed trend flip closes the current episode and opens the next;
/// extending and a same-day re-run write nothing).
/// </summary>
public class RegimeLabelServiceTests
{
    private static readonly DateOnly Start = new(2000, 1, 1);
    private static string D(int i) => Start.AddDays(i).ToString("yyyy-MM-dd");
    private const string Obs0 = "2013-01-01T00:00:00Z";       // single backfill observation stamp
    private const string Wm = "2013-06-01T00:00:00Z";         // a watermark after Obs0

    private static RegimeLabelService NewService(AlphaLabDbContext db, RegimeOptions? opts = null)
    {
        opts ??= new RegimeOptions();
        return new RegimeLabelService(db, new BarReadService(db), new RegimeProxyReadiness(db, opts), opts);
    }

    // Register the GSPC proxy + its versioned config row (the FR-38 precedent), returning the proxy id.
    private static long RegisterProxy(AlphaLabDbContext db) =>
        new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, D(0));

    private static void SeedBars(AlphaLabDbContext db, long id, int count, Func<int, double> price, string observedAt = Obs0)
    {
        for (var i = 0; i < count; i++)
        {
            var c = price(i);
            db.Bars.Add(new BarRow
            {
                SecurityId = id, Date = D(i), Version = 1, ObservedAt = observedAt,
                Close = c, AdjClose = c, Source = RegimeProxySource.EodhdGspc
            });
        }
        db.SaveChanges();
    }

    // A gently rising series ≥ the 956-session warm-up, so a label is computable at the last session.
    private static double Rising(int i) => 100.0 * Math.Pow(1.0002, i);

    // ---- fail closed: no resolved proxy id ⇒ nothing computed, nothing written ----
    [Fact]
    public void FR26_FailsClosed_WhenProxyUnresolved()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var r = NewService(db).ComputeAndSave(D(959), Wm);

            Assert.False(r.Computed);
            Assert.Contains("not resolved", r.Reason);
            Assert.Empty(db.RegimeLabels.ToList());
            Assert.Empty(db.RegimeEpisodes.ToList());
        }
        finally { TestDb.Delete(path); }
    }

    // ---- fail closed: below the ≈3.8-year warm-up ⇒ nothing written ----
    [Fact]
    public void FR26_FailsClosed_BelowWarmup_WritesNothing()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = RegisterProxy(db);
            SeedBars(db, id, 100, Rising); // 100 << 956

            var r = NewService(db).ComputeAndSave(D(99), Wm);

            Assert.False(r.Computed);
            Assert.Contains("fail closed", r.Reason);
            Assert.Empty(db.RegimeLabels.ToList());
            Assert.Empty(db.RegimeEpisodes.ToList());
        }
        finally { TestDb.Delete(path); }
    }

    // ---- fail closed: the proxy has no bar on asOf (cannot label asOf on a stale prior session) ----
    [Fact]
    public void FR26_FailsClosed_WhenProxyHasNoBarOnAsOf()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = RegisterProxy(db);
            SeedBars(db, id, 960, Rising); // last bar is D(959)

            var r = NewService(db).ComputeAndSave(D(970), Wm); // readiness passes (960 ≥ 956) but no bar on D970

            Assert.False(r.Computed);
            Assert.Contains("no bar on", r.Reason);
            Assert.Empty(db.RegimeLabels.ToList());
        }
        finally { TestDb.Delete(path); }
    }

    // ---- proxy id resolves from the MAX(version) config row, not appsettings, not v1 ----
    [Fact]
    public void FR26_ResolvesProxyId_FromMaxVersionConfigRow()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            // A stale v1 pointing at a bogus id with NO bars — if the service read v1 it would fail readiness.
            db.Config.Add(new ConfigRow
            {
                Key = RegimeProxyIngestion.ProxyConfigKey, ValueJson = "999999", Version = 1,
                ChangedOn = D(0), Reason = "stale"
            });
            db.SaveChanges();

            // Registering the real proxy appends v2 = the real id (value changed), and seeds its bars.
            var id = RegisterProxy(db);
            Assert.True(db.Config.Count(c => c.Key == RegimeProxyIngestion.ProxyConfigKey) == 2);
            SeedBars(db, id, 960, Rising);

            var r = NewService(db).ComputeAndSave(D(959), Wm);

            Assert.True(r.Computed);                          // used v2 (the real id), else readiness would fail
            Assert.Equal(D(959), r.Label!.Date);
        }
        finally { TestDb.Delete(path); }
    }

    // ---- computes + persists the label at asOf (tokens valid, cross product, provenance stamped) ----
    [Fact]
    public void FR26_ComputesAndPersists_LabelAtAsOf()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = RegisterProxy(db);
            SeedBars(db, id, 960, Rising);

            var r = NewService(db).ComputeAndSave(D(959), Wm);

            Assert.True(r.Computed);
            var row = Assert.Single(db.RegimeLabels.ToList());
            Assert.Equal(D(959), row.AsOf);
            Assert.Contains(row.Trend, new[] { "bull", "bear" });
            Assert.Contains(row.Vol, new[] { "normal_vol", "high_vol" });
            Assert.Equal($"{row.Trend}/{row.Vol}", row.Label);
            Assert.False(string.IsNullOrWhiteSpace(row.InputsHash));
            Assert.Equal(64, row.InputsHash.Length); // SHA-256 hex
        }
        finally { TestDb.Delete(path); }
    }

    // ---- F-LEAK: the label at a fixed asOf is invariant to future bars + later-observed corrections;
    //      the watermark is part of the provenance (a different watermark ⇒ a different inputs_hash) ----
    [Fact]
    public void FLEAK_LabelAtAsOf_InvariantToFutureBarsAndLaterCorrections_AndWatermarkIsProvenance()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = RegisterProxy(db);
            SeedBars(db, id, 960, Rising); // observed at Obs0; last bar D(959)

            const string wmBefore = "2013-06-01T00:00:00Z";
            var r1 = NewService(db).ComputeAndSave(D(959), wmBefore);
            Assert.True(r1.Computed);

            // Inject data that must NOT leak into a computation pinned at wmBefore:
            //  (a) a FUTURE bar dated after asOf (excluded by date), observed at Obs0;
            //  (b) a CORRECTION to a bar ≤ asOf, observed AFTER wmBefore (invisible at wmBefore).
            db.Bars.Add(new BarRow
            {
                SecurityId = id, Date = D(960), Version = 1, ObservedAt = Obs0,
                Close = 500, AdjClose = 500, Source = RegimeProxySource.EodhdGspc
            });
            db.Bars.Add(new BarRow
            {
                SecurityId = id, Date = D(957), Version = 2, ObservedAt = "2014-01-01T00:00:00Z",
                Close = 999, AdjClose = 999, Source = RegimeProxySource.EodhdGspc
            });
            db.SaveChanges();

            var r2 = NewService(db).ComputeAndSave(D(959), wmBefore);
            Assert.Equal(r1.Label!.Label, r2.Label!.Label);      // label tokens unchanged — no leak
            Assert.Equal(r1.InputsHash, r2.InputsHash);          // byte-identical provenance at the same watermark

            // A later watermark that DOES see the correction is a distinct point-in-time — provenance differs.
            var r3 = NewService(db).ComputeAndSave(D(959), "2014-06-01T00:00:00Z");
            Assert.True(r3.Computed);
            Assert.NotEqual(r1.InputsHash, r3.InputsHash);       // watermark is IN the hash (§20.1)
        }
        finally { TestDb.Delete(path); }
    }

    // ---- D45 episodes: a confirmed trend flip closes the current episode and opens the next; extending
    //      and a same-day re-run write nothing (forward-only, idempotent) ----
    [Fact]
    public void FR26_D45_Episodes_FlipClosesCurrentAndOpensNew_ExtendAndReRunAreNoops()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = RegisterProxy(db);

            // Flat (seeds bear: 100 is not > its own SMA) for the whole warm-up, then a rising ramp that
            // confirms a bear→bull flip a few sessions in.
            const int flat = 956;
            double Price(int i) => i < flat ? 100.0 : 100.0 * Math.Pow(1.02, i - flat + 1);
            SeedBars(db, id, flat + 40, Price);

            // Find the flip date with the SAME params the service uses (sma 200, confirm 5, …).
            var prms = new RegimeLabelParams(200, 1.0, 5, 21, 80, 3 * 252);
            var series = Enumerable.Range(0, flat + 40).Select(i => new ProxyClose(D(i), Price(i))).ToList();
            var trend = RegimeLabeler.TrendSeries(series, prms);
            var flipIdx = FindFirstFlipIndex(trend);
            var flipDate = trend[flipIdx].Date;
            var preDate = trend[flipIdx - 1].Date;
            var nextDate = trend[flipIdx + 1].Date;

            var svc = NewService(db);

            // 1) preDate: bear. Opens the first (ongoing) episode.
            var rPre = svc.ComputeAndSave(preDate, Wm);
            Assert.True(rPre.Computed);
            Assert.Equal("bear", rPre.Label!.TrendToken);
            var e1 = db.RegimeEpisodes.OrderBy(x => x.EpisodeId).ToList();
            Assert.Single(e1);
            Assert.Equal("bear", e1[0].Label);
            Assert.Equal(preDate, e1[0].StartDate);
            Assert.Null(e1[0].EndDate);

            // 2) flipDate: bull. Closes the bear episode at preDate, opens an ongoing bull episode.
            var rFlip = svc.ComputeAndSave(flipDate, Wm);
            Assert.Equal("bull", rFlip.Label!.TrendToken);
            var e2 = db.RegimeEpisodes.OrderBy(x => x.EpisodeId).ToList();
            Assert.Equal(2, e2.Count);
            Assert.Equal("bear", e2[0].Label);
            Assert.Equal(preDate, e2[0].EndDate);     // closed at the last bear session
            Assert.Equal("bull", e2[1].Label);
            Assert.Equal(flipDate, e2[1].StartDate);
            Assert.Null(e2[1].EndDate);

            // 3) nextDate: still bull. Extends the ongoing episode — no new row.
            svc.ComputeAndSave(nextDate, Wm);
            Assert.Equal(2, db.RegimeEpisodes.Count());

            // 4) re-run flipDate: idempotent — the ongoing episode already holds bull, so no write.
            svc.ComputeAndSave(flipDate, Wm);
            Assert.Equal(2, db.RegimeEpisodes.Count());
        }
        finally { TestDb.Delete(path); }
    }

    private static int FindFirstFlipIndex(IReadOnlyList<RegimeTrendPoint> trend)
    {
        for (var i = 1; i < trend.Count; i++)
            if (trend[i].Trend != trend[i - 1].Trend) return i;
        throw new InvalidOperationException("test series produced no trend flip — adjust the ramp.");
    }
}
