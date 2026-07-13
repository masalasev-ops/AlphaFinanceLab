using System.Net;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Http;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Unit tests for the Phase-1 shared plumbing: the hand-rolled resilient HTTP client (retry +
/// circuit breaker, decision #2), the raw-cache archive, and the api_usage_log headroom/writer.
/// All deterministic and offline — the delay/jitter sources are injected so tests never sleep.
/// </summary>
public class PlumbingTests
{
    // ---- A stub handler that plays a scripted sequence of behaviors per request ----
    private sealed class StubHandler(Func<int, HttpResponseMessage> behavior) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var i = Calls++;
            return Task.FromResult(behavior(i)); // behavior may throw to simulate a transport failure
        }
    }

    private static ResilientHttpClient NewClient(StubHandler handler, ResilientHttpOptions? opts = null) =>
        new(new HttpClient(handler), opts ?? new ResilientHttpOptions(),
            delay: (_, _) => Task.CompletedTask, // never actually sleep in tests
            jitter: () => 0.0);

    [Fact]
    public async Task ResilientHttpClient_RetriesTransientFailures_ThenSucceeds()
    {
        // Fail twice, then 200 "ok". MaxRetries default 3 ⇒ succeeds on the 3rd attempt.
        var handler = new StubHandler(i => i < 2
            ? throw new HttpRequestException("boom")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var client = NewClient(handler);

        var body = await client.GetStringAsync("https://x/y", "eodhd");

        Assert.Equal("ok", body);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ResilientHttpClient_ExhaustsRetries_ThrowsHttpFetch()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("always"));
        var client = NewClient(handler);

        await Assert.ThrowsAsync<HttpFetchException>(() => client.GetStringAsync("https://x/y", "eodhd"));
        Assert.Equal(4, handler.Calls); // 1 initial + 3 retries
    }

    [Fact]
    public async Task ResilientHttpClient_OpensCircuit_AfterConsecutiveFailures()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("always"));
        var opts = new ResilientHttpOptions { CircuitBreakThreshold = 5 };
        var client = NewClient(handler, opts);

        // 5 fully-failed fetches trip the breaker (each does 4 attempts).
        for (var n = 0; n < 5; n++)
            await Assert.ThrowsAsync<HttpFetchException>(() => client.GetStringAsync("https://x/y", "eodhd"));

        var callsBefore = handler.Calls;
        // The 6th call short-circuits: CircuitOpenException, no new HTTP attempt.
        await Assert.ThrowsAsync<CircuitOpenException>(() => client.GetStringAsync("https://x/y", "eodhd"));
        Assert.Equal(callsBefore, handler.Calls);
    }

    [Fact]
    public async Task ResilientHttpClient_Non2xx_IsRetriedThenFails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = NewClient(handler);

        await Assert.ThrowsAsync<HttpFetchException>(() => client.GetStringAsync("https://x/y", "eodhd"));
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public void FileRawCache_WritesPayloadUnderSourceAndDate()
    {
        var root = Path.Combine(Path.GetTempPath(), "alphalab-rawcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileRawCache(root);
            cache.Save("eodhd", "2026-07-13", "AAPL.eod.json", "[{\"date\":\"2026-07-10\"}]");

            var path = Path.Combine(root, "eodhd", "2026-07-13", "AAPL.eod.json");
            Assert.True(File.Exists(path));
            Assert.Contains("2026-07-10", File.ReadAllText(path));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    [Theory]
    [InlineData(500, 1000, true)]   // exactly 50% used ⇒ 50% headroom ⇒ OK
    [InlineData(501, 1000, false)]  // just over half ⇒ under 50% headroom
    [InlineData(0, 0, false)]       // unknown limit ⇒ fail closed
    [InlineData(10, -5, false)]     // nonsense limit ⇒ fail closed
    public void ApiUsageHeadroom_MatchesFiftyPercentRule(int calls, int planLimit, bool expected)
    {
        Assert.Equal(expected, ApiUsageHeadroom.HasHeadroom(calls, planLimit));
    }

    [Fact]
    public void ApiUsageLogWriter_UpsertsSingleRowPerAsOfSource()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "alphalab-usage-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = NewCtx(dbPath)) db.Database.Migrate();

            using (var db = NewCtx(dbPath))
            {
                var writer = new ApiUsageLogWriter(db);
                writer.Record("2026-07-13", "eodhd", 120, 100000);
                db.SaveChanges();
            }
            using (var db = NewCtx(dbPath))
            {
                var writer = new ApiUsageLogWriter(db);
                writer.Record("2026-07-13", "eodhd", 260, 100000); // same key ⇒ update, not a 2nd row
                db.SaveChanges();
            }
            using (var db = NewCtx(dbPath))
            {
                var row = Assert.Single(db.ApiUsageLog.ToList());
                Assert.Equal(260, row.Calls);
                Assert.Equal(100000, row.PlanLimit);
            }
        }
        finally
        {
            foreach (var s in new[] { "", "-wal", "-shm" })
                try { if (File.Exists(dbPath + s)) File.Delete(dbPath + s); } catch { /* best effort */ }
        }
    }

    private static AlphaLabDbContext NewCtx(string dbPath) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={dbPath}").Options);
}
