using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// P1R-11 (finding 150): the --preflight live-source check. These tests are fixture-backed — a good fixture
/// passes, a drifted one reports a NAMED failure (not a crash). The live run against EODHD/BlackRock/Wikipedia
/// is the operator's; here we prove the check LOGIC: count-band, adjusted_close presence, the finding-139
/// unadjustedValue precondition, the calendar-age WARN, and the write-free DB-path probe.
///
/// Note the probe windows are ignored by StubHttp (it returns the whole fixture regardless of from/to) — the
/// eod-short / div-wide asymmetry is a live-call cost/correctness point, tested for intent in Program.cs, not here.
/// </summary>
public sealed class BackfillPreflightTests
{
    private sealed class StubHttp : IResilientHttpClient
    {
        private readonly List<(string Needle, Func<string> Body)> _routes = [];

        // Replace-or-add by needle, so a test can override one HealthyHttp() route to drift a single source.
        public StubHttp Route(string needle, Func<string> body)
        {
            _routes.RemoveAll(r => r.Needle == needle);
            _routes.Add((needle, body));
            return this;
        }

        public Task<string> GetStringAsync(string url, string source, CancellationToken ct = default)
        {
            foreach (var (needle, body) in _routes)
                if (url.Contains(needle, StringComparison.Ordinal)) return Task.FromResult(body());
            throw new InvalidOperationException($"No route for {url}");
        }
    }

    private const string AsOf = "2026-07-15";
    private const int ReviewedThroughYear = 2025;

    // A StubHttp wired with every real fixture for a clean pass. Individual tests override one route to drift it.
    private static StubHttp HealthyHttp() => new StubHttp()
        .Route("get-fund-document", () => Fixtures.Holdings("OEF_holdings.csv"))
        .Route("wikipedia.org", () => Fixtures.Wikipedia("sp100_components.html"))
        .Route("/eod/AAPL.US", () => Fixtures.Eodhd("eod_AAPL_adjusted.json"))
        .Route("/div/AAPL.US", () => Fixtures.Eodhd("div_AAPL.json"))
        .Route("/eod/GSPC.INDX", () => Fixtures.Eodhd("eod_GSPC_INDX.json"));

    private static PreflightInputs Inputs(
        IResilientHttpClient http,
        string connectionString,
        int[] band,
        int asOfYear = 2026)
    {
        var eodhd = new EodhdOptions { ApiToken = "test-token" };
        return new PreflightInputs(
            ConnectionString: connectionString,
            ArenaId: "sp500",
            MembershipPrimary: new ISharesHoldingsMembershipProvider(http, ISharesHoldingsOptions.Oef(), NullRawCache.Instance),
            MembershipCrossCheck: new WikipediaMembershipCrossCheck(http, new WikipediaMembershipOptions
            {
                Url = "https://en.wikipedia.org/wiki/S%26P_100",
                Source = "wikipedia_sp100"
            }, NullRawCache.Instance),
            MarketData: new EodhdMarketDataProvider(http, eodhd, NullRawCache.Instance),
            RegimeProxy: new EodhdGspcRegimeProxyProvider(http, eodhd, NullRawCache.Instance),
            CountBand: band,
            ProbeSymbol: "AAPL",
            EodProbeFrom: "2026-07-05",
            DivProbeFrom: "2006-07-15",
            AsOf: AsOf,
            AsOfYear: asOfYear,
            ReviewedThroughYear: ReviewedThroughYear);
    }

    /// <summary>A writable temp dir → a connection string whose store dir is creatable. Returned so the test
    /// can delete it.</summary>
    private static (string ConnectionString, string Dir) TempStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alphalab-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return ($@"Data Source={Path.Combine(dir, "{Arena.Id}", "alphalab.db")}", dir);
    }

    [Fact]
    public async Task Preflight_AllSourcesHealthy_AllPass()
    {
        var (cs, dir) = TempStore();
        try
        {
            // asOfYear within the reviewed-through year (2025) so even the calendar-age check is Pass, not Warn.
            var report = await BackfillPreflight.RunAsync(Inputs(HealthyHttp(), cs, [99, 103], asOfYear: 2025));

            Assert.False(BackfillPreflight.HasFailure(report), FormatFor(report));
            Assert.All(report, r => Assert.Equal(PreflightStatus.Pass, r.Status));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Preflight_CountOutOfBand_Fails()
    {
        var (cs, dir) = TempStore();
        try
        {
            // The OEF/Wikipedia fixtures carry ~100 names; ask for the S&P 500 band -> both memberships breach.
            var report = await BackfillPreflight.RunAsync(Inputs(HealthyHttp(), cs, [495, 510]));

            Assert.True(BackfillPreflight.HasFailure(report));
            Assert.Contains(report, r => r.Check.StartsWith("oef-csv", StringComparison.Ordinal)
                                         && r.Status == PreflightStatus.Fail
                                         && r.Detail.Contains("OUTSIDE", StringComparison.Ordinal));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Preflight_MissingAdjustedClose_Fails()
    {
        var (cs, dir) = TempStore();
        try
        {
            var http = HealthyHttp().Route("/eod/AAPL.US", () =>
                /*lang=json*/ """[{"date":"2026-07-14","open":1.0,"high":2.0,"low":0.5,"close":1.5,"volume":1000}]""");

            var report = await BackfillPreflight.RunAsync(Inputs(http, cs, [99, 103]));

            var eod = Assert.Single(report, r => r.Check.StartsWith("eod AAPL.US", StringComparison.Ordinal));
            Assert.Equal(PreflightStatus.Fail, eod.Status);
            Assert.Contains("adjusted_close", eod.Detail, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Preflight_MissingUnadjustedValue_Fails()
    {
        var (cs, dir) = TempStore();
        try
        {
            // A dividend with no unadjustedValue: ParseDividends throws (finding 139) -> a NAMED Fail here.
            var http = HealthyHttp().Route("/div/AAPL.US", () =>
                /*lang=json*/ """[{"date":"2020-01-01","value":0.25}]""");

            var report = await BackfillPreflight.RunAsync(Inputs(http, cs, [99, 103]));

            var div = Assert.Single(report, r => r.Check.StartsWith("div AAPL.US", StringComparison.Ordinal));
            Assert.Equal(PreflightStatus.Fail, div.Status);
            Assert.Contains("unadjustedValue", div.Detail, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData(2026, PreflightStatus.Warn)] // as-of year past the closure list's reviewed-through (2025)
    [InlineData(2025, PreflightStatus.Pass)] // at the reviewed-through year — still fine
    public async Task Preflight_CalendarAge_WarnsPastReviewedYear(int asOfYear, PreflightStatus expected)
    {
        var (cs, dir) = TempStore();
        try
        {
            var report = await BackfillPreflight.RunAsync(Inputs(HealthyHttp(), cs, [99, 103], asOfYear));

            var cal = Assert.Single(report, r => r.Check == "calendar-age");
            Assert.Equal(expected, cal.Status);
            Assert.False(BackfillPreflight.HasFailure(report)); // a WARN never fails the run
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Preflight_DbPath_Writable_Passes()
    {
        var (cs, dir) = TempStore();
        try
        {
            var report = await BackfillPreflight.RunAsync(Inputs(HealthyHttp(), cs, [99, 103]));

            var db = Assert.Single(report, r => r.Check == "db-path");
            Assert.Equal(PreflightStatus.Pass, db.Status);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Preflight_DbPath_ParentIsFile_Fails()
    {
        var (cs, dir) = TempStore();
        var file = Path.Combine(dir, "not-a-dir");
        File.WriteAllText(file, string.Empty);
        try
        {
            // Store dir sits UNDER an existing file -> not creatable. Deterministic, cross-platform.
            var csUnderFile = $@"Data Source={Path.Combine(file, "sp500", "alphalab.db")}";
            var report = await BackfillPreflight.RunAsync(Inputs(HealthyHttp(), csUnderFile, [99, 103]));

            var db = Assert.Single(report, r => r.Check == "db-path");
            Assert.Equal(PreflightStatus.Fail, db.Status);
            Assert.Contains("is a file", db.Detail, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static string FormatFor(IReadOnlyList<PreflightResult> report) =>
        string.Join("; ", report.Select(r => $"{r.Status} {r.Check}: {r.Detail}"));
}
