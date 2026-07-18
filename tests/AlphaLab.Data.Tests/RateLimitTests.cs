using System.Net;
using AlphaLab.Data.Http;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The X-RateLimit reactive throttle (checkpoint 2.12 / INTEGRATIONS §1: the 1,000/min limit, independent
/// of the daily cap). The pure cooldown decision is tested directly; the wiring is tested through a stub
/// handler that carries the header, with an injected delay so no test actually sleeps.
/// </summary>
public class RateLimitTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);

    [Fact]
    public void CooldownFor_AtOrBelowFloor_ReturnsTheCooldown()
    {
        Assert.Equal(Cooldown, RateLimitGuard.CooldownFor(10, 50, Cooldown));
        Assert.Equal(Cooldown, RateLimitGuard.CooldownFor(50, 50, Cooldown)); // boundary: <= floor
    }

    [Fact]
    public void CooldownFor_AboveFloorOrUnknownOrDisabled_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, RateLimitGuard.CooldownFor(900, 50, Cooldown)); // plenty remaining
        Assert.Equal(TimeSpan.Zero, RateLimitGuard.CooldownFor(null, 50, Cooldown)); // header absent
        Assert.Equal(TimeSpan.Zero, RateLimitGuard.CooldownFor(0, 0, Cooldown));     // floor 0 disables
    }

    [Fact]
    public async Task GetStringAsync_LowRemaining_ThrottlesAndRecordsHeaders()
    {
        var delays = new List<TimeSpan>();
        var client = MakeClient(remaining: "10", limit: "1000", delays);

        var body = await client.GetStringAsync("https://eodhd.test/eod/X", "eodhd");

        Assert.Equal("payload", body);
        Assert.Equal(1000, client.LastRateLimitLimit);
        Assert.Equal(10, client.LastRateLimitRemaining);
        Assert.Contains(Cooldown, delays); // it paused because remaining (10) <= floor (50)
    }

    [Fact]
    public async Task GetStringAsync_HighRemaining_DoesNotThrottle()
    {
        var delays = new List<TimeSpan>();
        var client = MakeClient(remaining: "900", limit: "1000", delays);

        _ = await client.GetStringAsync("https://eodhd.test/eod/X", "eodhd");

        Assert.Equal(900, client.LastRateLimitRemaining);
        Assert.Empty(delays); // no pause
    }

    private static ResilientHttpClient MakeClient(string remaining, string limit, List<TimeSpan> delays)
    {
        var handler = new StubHandler(remaining, limit);
        var http = new HttpClient(handler);
        var opts = new ResilientHttpOptions { RateLimitRemainingFloor = 50, RateLimitCooldown = Cooldown };
        // Record-only delay so the test never actually waits.
        return new ResilientHttpClient(http, opts, delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });
    }

    private sealed class StubHandler(string remaining, string limit) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("payload") };
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Limit", limit);
            return Task.FromResult(resp);
        }
    }
}
