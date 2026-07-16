using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The 1.10 bootstrap CLI orchestration (BackfillRunner), exercised end-to-end OFFLINE: a fixture-backed
/// IResilientHttpClient feeds the REAL providers (OEF holdings, Wikipedia S&amp;P 100, GSPC.INDX EOD, AAPL
/// EOD/div/splits) through the runner's steps into a throwaway DB, so the whole wiring — right portfolioId,
/// count band, GSPC no-suffix URL, versioned bars, sectors, api_usage_log headroom — is verified without a
/// network. The live sp100 run is the operator's (decision #1). Dry-run makes no call and no write.
/// </summary>
public class BackfillRunnerTests
{
    private const string AsOf = "2026-07-13";

    // Byte-real fixtures routed by URL substring; unknown URLs fail loud (surfacing a mis-wired provider).
    private sealed class FixtureHttpClient : IResilientHttpClient
    {
        private readonly List<(string Needle, Func<string> Body)> _routes = [];
        public int Calls { get; private set; }

        public FixtureHttpClient Route(string needle, Func<string> body) { _routes.Add((needle, body)); return this; }

        public Task<string> GetStringAsync(string url, string source, CancellationToken ct = default)
        {
            Calls++;
            foreach (var (needle, body) in _routes)
            {
                if (url.Contains(needle, StringComparison.Ordinal)) return Task.FromResult(body());
            }
            // A member with no byte-real fixture returns an empty EODHD array (0 bars/divs/splits) so a full
            // RunAsync over the ~102-member roster completes offline; membership/proxy URLs must be routed.
            if (url.Contains("/eod/", StringComparison.Ordinal) || url.Contains("/div/", StringComparison.Ordinal)
                || url.Contains("/splits/", StringComparison.Ordinal))
            {
                return Task.FromResult("[]");
            }
            throw new InvalidOperationException($"No fixture route for URL: {url}");
        }
    }

    private static FixtureHttpClient FullClient() => new FixtureHttpClient()
        .Route("/eod/GSPC.INDX", () => Fixtures.Eodhd("eod_GSPC_INDX.json"))
        .Route("/eod/AAPL.US", () => Fixtures.Eodhd("eod_AAPL.json"))
        .Route("/div/AAPL.US", () => Fixtures.Eodhd("div_AAPL.json"))
        .Route("/splits/AAPL.US", () => Fixtures.Eodhd("splits_AAPL.json"))
        .Route("portfolioId=239723", () => Fixtures.Holdings("OEF_holdings.csv"))
        .Route("S%26P_100", () => Fixtures.Wikipedia("sp100_components.html"));

    private static readonly EodhdOptions Eodhd = new() { ApiToken = "test-token" };

    private static IRegimeProxyProvider Gspc(IResilientHttpClient http) => new EodhdGspcRegimeProxyProvider(http, Eodhd);
    private static IMarketDataProvider Market(IResilientHttpClient http) => new EodhdMarketDataProvider(http, Eodhd);
    private static IIndexMembershipProvider Oef(IResilientHttpClient http) => new ISharesHoldingsMembershipProvider(http, ISharesHoldingsOptions.Oef());
    private static IIndexMembershipProvider WikiSp100(IResilientHttpClient http) =>
        new WikipediaMembershipCrossCheck(http, new WikipediaMembershipOptions { Url = "https://en.wikipedia.org/wiki/S%26P_100", Source = "wikipedia_sp100" });

    private static BackfillRunner Runner(AlphaLabDbContext db, IResilientHttpClient http,
        IIndexMembershipProvider? primary = null, IIndexMembershipProvider? cross = null) =>
        new(db, primary ?? Oef(http), cross ?? WikiSp100(http), Gspc(http), Market(http));

    // Simulates the Stage-1 Wikipedia 403: a cross-check fetch that fails after retries.
    private sealed class ThrowingMembershipProvider(string source) : IIndexMembershipProvider
    {
        public Task<MembershipSnapshot> GetMembersAsync(string asOf, CancellationToken ct = default) =>
            throw new HttpFetchException($"https://en.wikipedia.org/wiki/{source}", new Exception("simulated 403"));
    }

    [Fact]
    public void SeedCalendarStep_SeedsTheWindow()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var o = new BackfillOptions { AsOf = AsOf, CalendarYearsEitherSide = 2 }; // 2024..2028 (keep it quick)
            var n = Runner(db, http).SeedCalendarStep(o);
            Assert.True(n > 1200);                                            // ~252/yr x 5
            Assert.True(db.TradingCalendar.Any(r => r.Date == "2024-11-29")); // a known half-day is present
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public async Task BackfillRegimeProxyStep_ResolvesAndIngestsGspc()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var runner = Runner(db, http);
            var o = new BackfillOptions { AsOf = AsOf };

            await runner.BackfillRegimeProxyStep(o);

            var proxy = db.Securities.Single(s => s.CurrentSymbol == "GSPC.INDX");
            Assert.Equal("INDX", proxy.Exchange);
            Assert.Equal(62, db.Bars.Count(b => b.SecurityId == proxy.SecurityId));
            Assert.Single(db.Config.Where(c => c.Key == RegimeProxyIngestion.ProxyConfigKey).ToList());
            Assert.Equal(1, runner.ApiCalls["eodhd_gspc"]);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public async Task RefreshMembershipStep_RealOefVsWikipedia_ReconcilesAndLogs()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var runner = Runner(db, http);

            var result = await runner.RefreshMembershipStep(new BackfillOptions { AsOf = AsOf });

            Assert.NotEmpty(db.IndexMembershipLog.ToList());        // an audit row either way (apply or held)
            Assert.Equal(1, runner.ApiCalls["oef_csv"]);
            Assert.Equal(1, runner.ApiCalls["wikipedia_sp100"]);
            Assert.InRange(result.PrimaryCount, 99, 103);          // OEF slice count sanity
        }
        finally { TestDb.Delete(path); }
    }

    // Agreeing rosters (OEF cross-checked against itself) exercise the APPLY + sector-ingestion path.
    [Fact]
    public async Task RefreshMembershipStep_AgreeingRosters_AppliesMembersAndSectors()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var runner = Runner(db, http, primary: Oef(http), cross: Oef(http)); // identical -> agreement

            var result = await runner.RefreshMembershipStep(new BackfillOptions { AsOf = AsOf });

            Assert.True(result.Applied);
            Assert.InRange(db.IndexMembership.Count(m => m.RemovedOn == null), 99, 103);
            var aapl = db.Securities.Single(s => s.CurrentSymbol == "AAPL");
            Assert.Equal("Information Technology", aapl.Sector); // sector applied from the OEF Sector column
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void SeedHistoricalMembershipStep_IngestsIntervals()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var n = Runner(db, FullClient()).SeedHistoricalMembershipStep(
                Fixtures.Historical("S_P_500_Historical_Components___Changes__Updated.csv"));
            Assert.True(n > 500);                        // thousands of historical intervals
            Assert.True(db.IndexMembership.Any());
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public async Task BackfillSecurityStep_IngestsBarsAndCorporateActions()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var runner = Runner(db, http);
            var id = new SecurityMaster(db).Register("AAPL", "US", "2000-01-01");

            await runner.BackfillSecurityStep(id, "AAPL", new BackfillOptions { AsOf = AsOf });

            Assert.True(db.Bars.Count(b => b.SecurityId == id) > 0);
            Assert.True(db.CorporateActions.Any(c => c.SecurityId == id && c.Type == "dividend"));
            Assert.True(db.CorporateActions.Any(c => c.SecurityId == id && c.Type == "split"));
            Assert.Equal(3, runner.ApiCalls["eodhd"]); // eod + div + splits
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public async Task FlushApiUsage_RecordsCallsAndFlagsHeadroomBreach()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var runner = Runner(db, http);
            var id = new SecurityMaster(db).Register("AAPL", "US", "2000-01-01");
            var o = new BackfillOptions { AsOf = AsOf, ApiPlanLimit = 4 };

            await runner.BackfillSecurityStep(id, "AAPL", o); // 3 eodhd calls
            await runner.BackfillRegimeProxyStep(o);          // 1 eodhd_gspc call

            var breached = runner.FlushApiUsage(o);

            Assert.Contains("eodhd", breached);                     // 3 > 50% of 4
            Assert.DoesNotContain("eodhd_gspc", breached);          // 1 <= 50% of 4
            Assert.Equal(3, db.ApiUsageLog.Find(AsOf, "eodhd")!.Calls);
            Assert.Equal(1, db.ApiUsageLog.Find(AsOf, "eodhd_gspc")!.Calls);
        }
        finally { TestDb.Delete(path); }
    }

    // P1R-2: the ≥50% headroom check reads the ACCUMULATED EODHD-family day total (from the DB), not a
    // single run's spend — so two same-day runs each individually under the limit still breach once their
    // sum crosses it. Fails on the pre-P1R-2 code, which checked run-local spend and overwrote the row.
    [Fact]
    public async Task FlushApiUsage_HeadroomChecksAccumulatedDayTotal_AcrossRuns()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var id = new SecurityMaster(db).Register("AAPL", "US", "2000-01-01");
            var o = new BackfillOptions { AsOf = AsOf, ApiPlanLimit = 8 };

            // Run 1: 3 eodhd calls; day total 3 <= 50% of 8 -> no breach.
            var runner1 = Runner(db, http);
            await runner1.BackfillSecurityStep(id, "AAPL", o);
            Assert.DoesNotContain("eodhd", runner1.FlushApiUsage(o));

            // Run 2 (fresh runner, same DB + as_of): another 3 -> day total 6 > 50% of 8 -> breach.
            var runner2 = Runner(db, http);
            await runner2.BackfillSecurityStep(id, "AAPL", o);
            Assert.Contains("eodhd", runner2.FlushApiUsage(o));

            Assert.Equal(6, db.ApiUsageLog.Find(AsOf, "eodhd")!.Calls); // accumulated, not clobbered
        }
        finally { TestDb.Delete(path); }
    }

    // P1R-2 drain: FlushApiUsage empties the runner's counters after persisting, so calling it twice
    // records the spend once — the runner's finally-flush plus any explicit flush cannot double-count.
    [Fact]
    public async Task FlushApiUsage_CalledTwice_RecordsSpendOnce()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var id = new SecurityMaster(db).Register("AAPL", "US", "2000-01-01");
            var o = new BackfillOptions { AsOf = AsOf, ApiPlanLimit = 100000 };
            var runner = Runner(db, http);

            await runner.BackfillSecurityStep(id, "AAPL", o); // 3 eodhd
            runner.FlushApiUsage(o);
            runner.FlushApiUsage(o); // counters drained -> second flush adds zero

            Assert.Equal(3, db.ApiUsageLog.Find(AsOf, "eodhd")!.Calls);
        }
        finally { TestDb.Delete(path); }
    }

    // A cross-check fetch failure (the Stage-1 Wikipedia 403) aborts RunAsync — but usage accounting must
    // survive: FlushApiUsage runs in a finally and calls are counted at success, so the proxy (eodhd_gspc)
    // AND primary (oef_csv) calls already spent are recorded, while the cross that threw before its count
    // is not. Pins both the finally-flush and the count-at-success placement.
    [Fact]
    public async Task RunAsync_AbortedByCrossCheckFailure_StillFlushesUsageForCallsSpent()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var runner = new BackfillRunner(db, Oef(http),
                new ThrowingMembershipProvider("wikipedia_sp100"), Gspc(http), Market(http));
            var o = new BackfillOptions { AsOf = AsOf, ApiPlanLimit = 100000 };

            await Assert.ThrowsAsync<HttpFetchException>(() => runner.RunAsync(o));

            Assert.Equal(1, db.ApiUsageLog.Find(AsOf, "eodhd_gspc")!.Calls); // proxy fetched before the abort
            Assert.Equal(1, db.ApiUsageLog.Find(AsOf, "oef_csv")!.Calls);    // primary fetched before the abort
            Assert.Null(db.ApiUsageLog.Find(AsOf, "wikipedia_sp100"));       // cross threw before its count
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_DryRun_MakesNoCallAndNoWrite()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var runner = Runner(db, http);

            await runner.RunAsync(new BackfillOptions { AsOf = AsOf, DryRun = true });

            Assert.Equal(0, http.Calls);
            Assert.Empty(db.TradingCalendar.ToList());
            Assert.Empty(db.Securities.ToList());
            Assert.Empty(db.Config.Where(c => c.Key == RegimeProxyIngestion.ProxyConfigKey).ToList());
        }
        finally { TestDb.Delete(path); }
    }

    // Re-run safety (review #1): the forward bootstrap must NOT seed the historical S&P 500 roster (a Phase-4
    // replay prerequisite) — else a second run's reconcile would mass-evict those members. Two full RunAsyncs
    // over agreeing rosters leave the forward slice intact with zero drops and no historical intervals.
    [Fact]
    public async Task RunAsync_ReRun_ForwardOnly_NoEvictionAndNoHistorical()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var http = FullClient();
            var o = new BackfillOptions { AsOf = AsOf, CalendarYearsEitherSide = 1 }; // small calendar window

            await Runner(db, http, primary: Oef(http), cross: Oef(http)).RunAsync(o);
            var openAfterRun1 = db.IndexMembership.Count(m => m.RemovedOn == null);
            Assert.InRange(openAfterRun1, 99, 103);

            await Runner(db, http, primary: Oef(http), cross: Oef(http)).RunAsync(o); // re-run

            Assert.Equal(openAfterRun1, db.IndexMembership.Count(m => m.RemovedOn == null)); // no evictions
            Assert.DoesNotContain(db.IndexMembership.ToList(), m => m.RemovedOn != null);    // nothing dropped
            Assert.InRange(db.IndexMembership.Count(), 99, 103);                             // no historical intervals
        }
        finally { TestDb.Delete(path); }
    }
}
