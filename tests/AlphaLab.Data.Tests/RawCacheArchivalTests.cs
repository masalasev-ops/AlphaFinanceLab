using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// P1R-4: every raw provider payload is archived under the run's OBSERVATION day (asOf), not the query
/// bound. Before the fix /div and /splits filed under `from` (20 years off) while /eod and the GSPC proxy
/// filed under `to` — correct only because the backfill happened to pass to == asOf. This pins archival to
/// the contract's asOf, with to deliberately != asOf so the partition proves it's asOf (not to/from) that
/// drives it. Archival happens before parse, so the payload is captured regardless of parse outcome.
/// </summary>
public class RawCacheArchivalTests
{
    private sealed class StubHttp : IResilientHttpClient
    {
        private readonly List<(string Needle, Func<string> Body)> _routes = [];
        public StubHttp Route(string needle, Func<string> body) { _routes.Add((needle, body)); return this; }

        public Task<string> GetStringAsync(string url, string source, CancellationToken ct = default)
        {
            foreach (var (needle, body) in _routes)
                if (url.Contains(needle, StringComparison.Ordinal)) return Task.FromResult(body());
            throw new InvalidOperationException($"No route for {url}");
        }
    }

    [Fact]
    public async Task RawCache_ArchivesFixedSitesUnderObservationDate_NotQueryBound()
    {
        const string from = "2006-07-15", to = "2026-07-14", asOf = "2026-07-15"; // to != asOf on purpose
        var root = Path.Combine(Path.GetTempPath(), "alphalab-rawcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileRawCache(root);
            var options = new EodhdOptions { ApiToken = "test-token" };
            var http = new StubHttp()
                .Route("/eod/AAPL.US", () => Fixtures.Eodhd("eod_AAPL.json"))
                .Route("/div/AAPL.US", () => Fixtures.Eodhd("div_AAPL.json"))
                .Route("/splits/AAPL.US", () => Fixtures.Eodhd("splits_AAPL.json"))
                .Route("/eod/GSPC.INDX", () => Fixtures.Eodhd("eod_GSPC_INDX.json"));

            var market = new EodhdMarketDataProvider(http, options, cache);
            await market.GetEodAsync("AAPL", from, to, asOf);
            await market.GetDividendsAsync("AAPL", from, asOf);
            await market.GetSplitsAsync("AAPL", from, asOf);

            var proxy = new EodhdGspcRegimeProxyProvider(http, options, cache);
            await proxy.GetProxyBarsAsync(from, to, asOf);

            // All four payload kinds land under {source}/{asOf}/ ...
            Assert.True(File.Exists(Path.Combine(root, "eodhd", asOf, "AAPL.eod.json")));
            Assert.True(File.Exists(Path.Combine(root, "eodhd", asOf, "AAPL.div.json")));
            Assert.True(File.Exists(Path.Combine(root, "eodhd", asOf, "AAPL.splits.json")));
            Assert.True(File.Exists(Path.Combine(root, "eodhd_gspc", asOf, "GSPC.INDX.eod.json")));

            // ... and NOTHING under the query-bound partitions (the old, buggy homes).
            Assert.False(Directory.Exists(Path.Combine(root, "eodhd", from)));    // div/splits used to land here
            Assert.False(Directory.Exists(Path.Combine(root, "eodhd", to)));      // eod used to land here
            Assert.False(Directory.Exists(Path.Combine(root, "eodhd_gspc", to))); // the proxy used to land here
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }
}
