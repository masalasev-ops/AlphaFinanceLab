using System.Net;
using System.Text;
using AlphaLab.Data.Http;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The binary fetch channel (checkpoint 6.6). Two properties are under test and they are different in
/// kind: that bytes arrive UNDECODED, and that the byte path is the SAME resilience policy as the text
/// path rather than a copy of it.
///
/// The second is the one worth a fixture. A duplicated retry loop passes every happy-path assertion and
/// fails only where it matters — a breaker that counts one path's failures and lets the other keep
/// hammering a dead endpoint. So the falsifier here trips the breaker through ONE path and asserts the
/// OTHER is open: that assertion is red under any implementation with two counters, and cannot be
/// satisfied by a byte path that merely looks correct.
/// </summary>
public class ResilientHttpBinaryTests
{
    /// <summary>Deterministic handler: replays a queued script of outcomes, one per attempt.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _n;
        public int Calls => _n;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = script[Math.Min(_n, script.Length - 1)];
            _n++;
            return Task.FromResult(step());
        }
    }

    private static Func<HttpResponseMessage> Ok(byte[] body) => () =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static Func<HttpResponseMessage> Fail() => () => throw new HttpRequestException("transport down");

    private static ResilientHttpClient Client(ScriptedHandler handler, int maxRetries = 0) =>
        new(new HttpClient(handler),
            new ResilientHttpOptions { MaxRetries = maxRetries, CircuitBreakThreshold = 2, RateLimitRemainingFloor = 0 },
            delay: (_, _) => Task.CompletedTask,   // never actually sleep
            jitter: () => 0.0);                    // deterministic backoff

    /// <summary>Bytes that a text decode destroys: the zip magic, then the whole high range. A UTF-8
    /// decode maps every invalid sequence to U+FFFD, so the round trip is lossy in a way that shows up
    /// as a corrupt archive rather than as an encoding error — which is why the channel exists at all.</summary>
    private static byte[] ZipShapedBytes()
    {
        var b = new byte[4 + 128];
        b[0] = 0x50; b[1] = 0x4B; b[2] = 0x03; b[3] = 0x04;   // "PK\x03\x04"
        for (var i = 0; i < 128; i++) b[4 + i] = (byte)(128 + i);
        return b;
    }

    [Fact]
    public async Task FR5_D41_GetBytes_ReturnsThePayloadUndecoded()
    {
        var payload = ZipShapedBytes();
        var h = new ScriptedHandler(Ok(payload));

        var got = await Client(h).GetBytesAsync("https://x/f.zip", "french");

        Assert.Equal(payload, got);
    }

    /// <summary>The reason the byte channel had to exist, demonstrated rather than asserted: the SAME
    /// payload through the text path does not survive. This is a characterization of the old path, so if
    /// someone later "simplifies" the binary fetch back onto GetStringAsync, this test says what breaks.</summary>
    [Fact]
    public async Task FR5_D41_TheTextPath_CorruptsTheSameBytes_WhichIsWhyTheChannelExists()
    {
        var payload = ZipShapedBytes();
        var h = new ScriptedHandler(Ok(payload));

        var asText = await Client(h).GetStringAsync("https://x/f.zip", "french");
        var roundTripped = Encoding.UTF8.GetBytes(asText);

        Assert.NotEqual(payload, roundTripped);
        Assert.Contains('�', asText);   // the replacement char: information already gone
    }

    // ---------- the falsifier: ONE policy, not two ----------

    [Fact]
    public async Task FR5_D41_FailuresOnTheBytePath_OpenTheBreakerForTheTextPath()
    {
        var h = new ScriptedHandler(Fail());
        var c = Client(h);   // CircuitBreakThreshold = 2, MaxRetries = 0

        await Assert.ThrowsAsync<HttpFetchException>(() => c.GetBytesAsync("https://x/1.zip", "french"));
        await Assert.ThrowsAsync<HttpFetchException>(() => c.GetBytesAsync("https://x/2.zip", "french"));

        // The counter is SHARED, so the text path is now refused without another request being made.
        var callsBefore = h.Calls;
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.GetStringAsync("https://x/3.json", "eodhd"));
        Assert.Equal(callsBefore, h.Calls);
    }

    [Fact]
    public async Task FR5_D41_FailuresOnTheTextPath_OpenTheBreakerForTheBytePath()
    {
        var h = new ScriptedHandler(Fail());
        var c = Client(h);

        await Assert.ThrowsAsync<HttpFetchException>(() => c.GetStringAsync("https://x/1.json", "eodhd"));
        await Assert.ThrowsAsync<HttpFetchException>(() => c.GetStringAsync("https://x/2.json", "eodhd"));

        var callsBefore = h.Calls;
        await Assert.ThrowsAsync<CircuitOpenException>(() => c.GetBytesAsync("https://x/3.zip", "french"));
        Assert.Equal(callsBefore, h.Calls);
    }

    [Fact]
    public async Task FR5_D41_ASuccessfulByteFetch_ResetsTheSharedBreaker()
    {
        var payload = ZipShapedBytes();
        var h = new ScriptedHandler(Fail(), Ok(payload), Fail());
        var c = Client(h);

        await Assert.ThrowsAsync<HttpFetchException>(() => c.GetBytesAsync("https://x/1.zip", "french"));
        Assert.Equal(payload, await c.GetBytesAsync("https://x/2.zip", "french"));   // resets the counter

        // One failure after the reset is below the threshold of 2, so this is a fetch failure, NOT an
        // open circuit — which is what proves the reset reached the shared counter.
        await Assert.ThrowsAsync<HttpFetchException>(() => c.GetBytesAsync("https://x/3.zip", "french"));
    }

    [Fact]
    public async Task FR5_D41_TheBytePath_RetriesTransientFailures_LikeTheTextPath()
    {
        var payload = ZipShapedBytes();
        var h = new ScriptedHandler(Fail(), Fail(), Ok(payload));

        var got = await Client(h, maxRetries: 3).GetBytesAsync("https://x/f.zip", "french");

        Assert.Equal(payload, got);
        Assert.Equal(3, h.Calls);   // two failures then the success: the retry loop is shared
    }
}
