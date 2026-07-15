using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FX-RegimeProxyBackfill (TEST_PLAN §2) — FR-38/D73. The regime proxy feed: parse the byte-real GSPC.INDX
/// EOD, ingest it under a permanent security_id, resolve Regime.ProxySecurityId into a versioned config row,
/// fail closed below the warm-up, and raise the SPY.US cross-check alarm on a divergent sample. The
/// self-built cap-weight fallback is a dormant seam.
/// </summary>
public class RegimeProxyTests
{
    private const string AsOf = "2026-07-13";
    private const int RequiredWarmup = 200 + 3 * 252; // TrendSmaDays + VolLookbackYears*252 = 956

    private static EodBar Bar(string date, double close) => new(date, null, null, null, close, close, 1000);

    // ---- Parse the byte-real GSPC.INDX payload (the primary proxy source) ----
    [Fact]
    public void ParseGspcIndx_RealPayload()
    {
        var bars = EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_GSPC_INDX.json"));
        Assert.Equal(62, bars.Count);
        Assert.Equal("2026-04-14", bars[0].Date);
        Assert.Equal(6967.3799, bars[0].Close);
        Assert.Equal(5032380000L, bars[0].Volume); // > Int32 — proves the long volume column (index turnover)
        Assert.Equal("2026-07-13", bars[^1].Date);
    }

    // ---- Resolve identity + persist Regime.ProxySecurityId as a versioned config row (idempotent) ----
    [Fact]
    public void ResolveProxySecurityId_RegistersGspc_AndWritesVersionedConfig_Idempotently()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            long id;
            using (var db = TestDb.Open(path))
            {
                id = new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, AsOf);
            }
            using (var db = TestDb.Open(path))
            {
                // GSPC.INDX registered verbatim under the INDX exchange.
                var sec = db.Securities.Single(s => s.SecurityId == id);
                Assert.Equal("GSPC.INDX", sec.CurrentSymbol);
                Assert.Equal("INDX", sec.Exchange);

                // A single versioned config row pointing at the proxy id.
                var cfg = Assert.Single(db.Config.Where(c => c.Key == RegimeProxyIngestion.ProxyConfigKey).ToList());
                Assert.Equal(1, cfg.Version);
                Assert.Equal(id.ToString(), cfg.ValueJson);
            }
            using (var db = TestDb.Open(path))
            {
                // Re-resolve is idempotent: same id, NO new config version.
                var again = new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, "2026-07-14");
                Assert.Equal(id, again);
                Assert.Single(db.Config.Where(c => c.Key == RegimeProxyIngestion.ProxyConfigKey).ToList());
            }
        }
        finally { TestDb.Delete(path); }
    }

    // ---- Ingest the real GSPC bars under the proxy id (versioned append-only, idempotent) ----
    [Fact]
    public void IngestProxyBars_RealGspc_VersionedAndIdempotent()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            var bars = EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_GSPC_INDX.json"));
            using var db = TestDb.Open(path);
            var ing = new RegimeProxyIngestion(db);
            var id = ing.ResolveProxySecurityId(RegimeProxySource.EodhdGspc, AsOf);

            Assert.Equal(62, ing.IngestProxyBars(id, bars, observedAt: "2026-07-13T22:00:00Z"));
            Assert.Equal(0, ing.IngestProxyBars(id, bars, observedAt: "2026-07-14T22:00:00Z")); // idempotent re-fetch
            Assert.Equal(62, db.Bars.Count(b => b.SecurityId == id));
        }
        finally { TestDb.Delete(path); }
    }

    // ---- Warm-up guard: fail closed on the real short history; ready at the full warm-up ----
    [Fact]
    public void Readiness_FailsClosed_OnRealShortHistory()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            var bars = EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_GSPC_INDX.json")); // 62 sessions
            using var db = TestDb.Open(path);
            var ing = new RegimeProxyIngestion(db);
            var id = ing.ResolveProxySecurityId(RegimeProxySource.EodhdGspc, AsOf);
            ing.IngestProxyBars(id, bars, "2026-07-13T22:00:00Z");

            var r = new RegimeProxyReadiness(db, new RegimeOptions()).CheckReadiness(id, AsOf);
            Assert.False(r.IsReady);
            Assert.Equal(62, r.SessionsAvailable);
            Assert.Equal(RequiredWarmup, r.SessionsRequired);
            Assert.Contains("fail closed", r.Reason);
        }
        finally { TestDb.Delete(path); }
    }

    [Theory]
    [InlineData(RequiredWarmup, true)]      // exactly the warm-up -> ready
    [InlineData(RequiredWarmup - 1, false)] // one session short -> fail closed
    public void Readiness_BracketsTheWarmupBoundary(int sessions, bool ready)
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var id = new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, "2000-01-01");
            var start = new DateOnly(2000, 1, 1);
            for (var i = 0; i < sessions; i++)
            {
                db.Bars.Add(new BarRow
                {
                    SecurityId = id,
                    Date = start.AddDays(i).ToString("yyyy-MM-dd"),
                    Version = 1,
                    ObservedAt = "2000-01-01T00:00:00Z",
                    Close = 100 + i % 7,
                    AdjClose = 100 + i % 7,
                    Source = "eodhd_gspc"
                });
            }
            db.SaveChanges();
            var asOf = start.AddDays(sessions - 1).ToString("yyyy-MM-dd");

            var r = new RegimeProxyReadiness(db, new RegimeOptions()).CheckReadiness(id, asOf);
            Assert.Equal(ready, r.IsReady);
            Assert.Equal(sessions, r.SessionsAvailable);
        }
        finally { TestDb.Delete(path); }
    }

    // ---- SPY.US cross-check: agree when returns track, alarm on a divergent sample ----
    [Fact]
    public void CrossCheck_Agrees_WhenReturnsTrack()
    {
        var gspc = new[] { Bar("2026-06-01", 100), Bar("2026-06-02", 101), Bar("2026-06-03", 102), Bar("2026-06-04", 103) };
        var spy = new[] { Bar("2026-06-01", 50), Bar("2026-06-02", 50.5), Bar("2026-06-03", 51.0), Bar("2026-06-04", 51.5) };

        var r = RegimeProxyCrossCheck.Compare(gspc, spy);
        Assert.True(r.Agreed);
        Assert.Null(r.Alarm);
        Assert.Equal(3, r.Compared);
    }

    [Fact]
    public void CrossCheck_Alarms_OnDivergentSample()
    {
        var gspc = new[] { Bar("2026-06-01", 100), Bar("2026-06-02", 101), Bar("2026-06-03", 102), Bar("2026-06-04", 103) };
        var spy = new[] { Bar("2026-06-01", 50), Bar("2026-06-02", 50.5), Bar("2026-06-03", 60.0), Bar("2026-06-04", 60.6) }; // 06-03 jumps

        var r = RegimeProxyCrossCheck.Compare(gspc, spy);
        Assert.False(r.Agreed);
        Assert.NotNull(r.Alarm);
        Assert.Contains("2026-06-03", r.Alarm);
    }

    // ---- The self-built cap-weight fallback is dormant; unknown sources fail loud ----
    [Fact]
    public void SelfBuiltCapWeightProxy_IsDormant_FailsLoud()
    {
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = new SelfBuiltCapWeightRegimeProxyProvider().GetProxyBarsAsync("2020-01-01", "2020-12-31", "2020-12-31");
        });
    }

    [Theory]
    [InlineData(RegimeProxySource.SelfBuiltCapWeight)]
    [InlineData("bogus_source")]
    public void ResolveProxySecurityId_InactiveOrUnknownSource_FailsLoud(string source)
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            Assert.Throws<NotSupportedException>(() => new RegimeProxyIngestion(db).ResolveProxySecurityId(source, AsOf));
        }
        finally { TestDb.Delete(path); }
    }

    // The proxy is a permanent singleton: a later resolve with an EARLIER asOf (a catch-up/replay of a past
    // session) must reuse the same id and write NO new config version — never mint a duplicate security.
    [Fact]
    public void ResolveProxySecurityId_DecreasingAsOf_SameId_NoNewVersion()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            long first, earlier;
            using (var db = TestDb.Open(path)) first = new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, "2026-07-13");
            using (var db = TestDb.Open(path)) earlier = new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, "2026-04-14"); // earlier
            using (var db = TestDb.Open(path))
            {
                Assert.Equal(first, earlier);
                Assert.Single(db.Securities.Where(s => s.CurrentSymbol == "GSPC.INDX").ToList()); // exactly one identity
                Assert.Single(db.Config.Where(c => c.Key == RegimeProxyIngestion.ProxyConfigKey).ToList());
            }
        }
        finally { TestDb.Delete(path); }
    }

    // A pre-existing config value change appends version+1 and leaves the prior row byte-unchanged (append-only).
    [Fact]
    public void ResolveProxySecurityId_ValueChange_AppendsVersion_NeverMutatesPrior()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using (var db = TestDb.Open(path))
            {
                db.Config.Add(new ConfigRow { Key = RegimeProxyIngestion.ProxyConfigKey, ValueJson = "999", Version = 1, ChangedOn = "2020-01-01", Reason = "seed" });
                db.SaveChanges();
            }
            long id;
            using (var db = TestDb.Open(path)) id = new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, AsOf);
            using (var db = TestDb.Open(path))
            {
                var rows = db.Config.Where(c => c.Key == RegimeProxyIngestion.ProxyConfigKey).ToList();
                Assert.Equal(2, rows.Count);
                var v1 = rows.Single(r => r.Version == 1);
                Assert.Equal("999", v1.ValueJson);                       // prior row untouched
                var v2 = rows.Single(r => r.Version == 2);
                Assert.Equal(id.ToString(), v2.ValueJson);               // MAX(version) resolves to the new id
            }
        }
        finally { TestDb.Delete(path); }
    }

    // The warm-up counts DISTINCT sessions, not bar rows: a correction (version 2) must not inflate the count,
    // and bars dated strictly after asOf must not leak into it.
    [Fact]
    public void Readiness_CountsDistinctSessions_ExcludesCorrectionsAndFutureBars()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            var bars = EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_GSPC_INDX.json")); // 62 sessions, ends 2026-07-13
            using var db = TestDb.Open(path);
            var ing = new RegimeProxyIngestion(db);
            var id = ing.ResolveProxySecurityId(RegimeProxySource.EodhdGspc, "2026-04-14");
            ing.IngestProxyBars(id, bars, "2026-07-13T22:00:00Z");

            // A correction to one bar spawns a Version=2 row on the same date.
            var corrected = bars.ToList();
            corrected[0] = bars[0] with { Close = bars[0].Close + 5.0 };
            Assert.Equal(1, ing.IngestProxyBars(id, corrected, "2026-07-14T22:00:00Z"));
            Assert.Equal(63, db.Bars.Count(b => b.SecurityId == id)); // 63 rows...

            var rdy = new RegimeProxyReadiness(db, new RegimeOptions());
            Assert.Equal(62, rdy.CheckReadiness(id, "2026-07-13").SessionsAvailable); // ...but 62 distinct sessions

            // A bar dated after asOf must not count toward an earlier asOf's warm-up.
            Assert.Equal(61, rdy.CheckReadiness(id, "2026-07-10").SessionsAvailable); // excludes 2026-07-13
        }
        finally { TestDb.Delete(path); }
    }

    // A gap in one feed must NOT be read as a divergence: the same into-date then spans different intervals.
    [Fact]
    public void CrossCheck_IntervalMismatch_IsNotAFalseAlarm()
    {
        var proxy = new[] { Bar("2026-06-01", 100), Bar("2026-06-02", 101), Bar("2026-06-03", 102) };
        var spy = new[] { Bar("2026-06-01", 50), Bar("2026-06-03", 51) }; // SPY missing 2026-06-02

        var r = RegimeProxyCrossCheck.Compare(proxy, spy);
        Assert.True(r.Agreed);   // 06-03 proxy return (1-day) vs SPY return (2-day) is skipped, not alarmed
        Assert.Null(r.Alarm);
        Assert.Equal(0, r.Compared);
    }

    [Fact]
    public void CrossCheck_ToleranceBoundary()
    {
        EodBar[] Proxy() => [Bar("2026-06-01", 100), Bar("2026-06-02", 101.0)];        // +1.00%
        var below = RegimeProxyCrossCheck.Compare(Proxy(), [Bar("2026-06-01", 100), Bar("2026-06-02", 100.55)], 0.5); // +0.55%, diff 0.45%
        var above = RegimeProxyCrossCheck.Compare(Proxy(), [Bar("2026-06-01", 100), Bar("2026-06-02", 100.35)], 0.5); // +0.35%, diff 0.65%
        Assert.True(below.Agreed);
        Assert.False(above.Agreed);
    }

    [Fact]
    public void CrossCheck_NoOverlap_IsVacuouslyAgreed()
    {
        var proxy = new[] { Bar("2026-06-01", 100), Bar("2026-06-02", 101) };
        var spy = new[] { Bar("2026-08-01", 50), Bar("2026-08-02", 50.5) }; // disjoint dates
        var r = RegimeProxyCrossCheck.Compare(proxy, spy);
        Assert.True(r.Agreed);
        Assert.Equal(0, r.Compared);
        Assert.Null(r.Alarm);
    }

    [Fact]
    public void CrossCheck_ReportsTheFirstDivergence()
    {
        var proxy = new[] { Bar("2026-06-01", 100), Bar("2026-06-02", 101), Bar("2026-06-03", 102), Bar("2026-06-04", 103) };
        var spy = new[] { Bar("2026-06-01", 50), Bar("2026-06-02", 50.5), Bar("2026-06-03", 60), Bar("2026-06-04", 72) }; // 06-03 AND 06-04 diverge

        var r = RegimeProxyCrossCheck.Compare(proxy, spy);
        Assert.False(r.Agreed);
        Assert.Contains("2026-06-03", r.Alarm);        // the earliest divergence
        Assert.DoesNotContain("2026-06-04", r.Alarm);
        Assert.Equal(2, r.Compared);                   // stopped at the first breach (06-02 ok, 06-03 breach)
    }
}
