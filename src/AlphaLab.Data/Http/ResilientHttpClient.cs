namespace AlphaLab.Data.Http;

/// <summary>Tuning for <see cref="ResilientHttpClient"/> (INTEGRATIONS §9 provider rules).</summary>
public sealed class ResilientHttpOptions
{
    /// <summary>Retries AFTER the first attempt (3 retries ⇒ up to 4 attempts). INTEGRATIONS §9.</summary>
    public int MaxRetries { get; init; } = 3;
    /// <summary>Base backoff; attempt n waits BaseDelay·2^n plus [0,1)·that as jitter.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Per-request timeout. INTEGRATIONS §9 = 30s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Consecutive fully-failed fetches that trip the breaker. INTEGRATIONS §9 = 5.</summary>
    public int CircuitBreakThreshold { get; init; } = 5;
    /// <summary>Descriptive User-Agent sent on every request. Wikimedia returns <b>403 Forbidden</b> to
    /// header-less requests (observed 2026-07-14 at first backfill; .NET's HttpClient sends no default
    /// User-Agent), which blocked the Wikipedia membership cross-check. A descriptive product token clears
    /// it (EODHD/BlackRock do not require one but receive it too). INTEGRATIONS §7/§9. Overridable, e.g.
    /// to add a contact per the Wikimedia UA policy.</summary>
    public string UserAgent { get; init; } = "AlphaLab/1.9 (paper-trading research lab)";

    /// <summary>When a response's <c>X-RateLimit-Remaining</c> drops to this many or fewer, the client
    /// pauses <see cref="RateLimitCooldown"/> before returning, spacing the next request out (INTEGRATIONS
    /// §1: the 1,000/min limit is independent of the daily cap). 0 disables the reactive throttle. The
    /// backfill's 304 single-threaded calls never approached the minute limit; the Phase-2 daily delta is
    /// the first burst-shaped workload that could, so the header is honoured from here on.</summary>
    public int RateLimitRemainingFloor { get; init; } = 50;

    /// <summary>The pause taken once remaining is at/below <see cref="RateLimitRemainingFloor"/>.</summary>
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Pure reactive-throttle arithmetic for the EODHD 1,000/min limit (INTEGRATIONS §1), split out so
/// the header→delay decision is unit-testable without a live endpoint.</summary>
public static class RateLimitGuard
{
    /// <summary>How long to pause after observing <paramref name="remaining"/> requests left this minute.
    /// Unknown (null — header absent/unparseable) or above the floor ⇒ no pause. A non-positive floor
    /// disables the throttle entirely.</summary>
    public static TimeSpan CooldownFor(int? remaining, int floor, TimeSpan cooldown)
        => floor > 0 && remaining is { } r && r <= floor ? cooldown : TimeSpan.Zero;
}

/// <summary>Thrown when the breaker is open (≥ threshold consecutive failures) — the daily run then
/// fails cleanly and catch-up recovers next day (INTEGRATIONS §9). Never a partial write.</summary>
public sealed class CircuitOpenException(string source, int consecutiveFailures)
    : Exception($"Circuit open for '{source}' after {consecutiveFailures} consecutive failures.")
{
    public int ConsecutiveFailures { get; } = consecutiveFailures;
}

/// <summary>Thrown when a fetch exhausts its retries. The URL's query string is stripped from the message
/// so a secret carried there (e.g. EODHD <c>?api_token=…</c>) never leaks to logs/stderr (D67, hard rule 11).</summary>
public sealed class HttpFetchException(string url, Exception inner)
    : Exception($"Fetch failed after retries: {RedactQuery(url)}", inner)
{
    private static string RedactQuery(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? url : string.Concat(url.AsSpan(0, q), "?<redacted>");
    }
}

/// <summary>Text-fetch contract every EODHD/BlackRock/Wikipedia provider goes through.</summary>
public interface IResilientHttpClient
{
    /// <summary>GET the URL as text (JSON or CSV). Retries transient failures with exponential
    /// backoff + jitter; opens a circuit after too many consecutive failures.</summary>
    Task<string> GetStringAsync(string url, string source, CancellationToken ct = default);
}

/// <summary>
/// The authenticated request-shaped client: arbitrary method, per-request headers, a body.
///
/// **A SEPARATE interface rather than more members on <see cref="IResilientHttpClient"/>, deliberately.**
/// Every provider before Phase 5 was a read-only GET against a URL that carried its own credential in the
/// query string, and widening the shared contract to add POST broke three existing test stubs that had no
/// reason to care — the interface would have grown a capability that all but one of its implementers must
/// then either fake or throw on. Narrow interfaces keep the blast radius of a new capability at the one
/// consumer that needs it.
///
/// **Retries a POST, and the safety argument is specific rather than general:** the two endpoints this
/// serves are Anthropic batch-create and single-message, both safe to repeat — a duplicate batch costs a
/// duplicate read the FR-21 cache then absorbs, whereas a lost batch is a no-read day. This is **not** a
/// licence to retry any POST; a future non-idempotent endpoint needs its own path.
/// </summary>
public interface IResilientHttpSender : IResilientHttpClient
{
    /// <param name="headers">Per-request headers (the API key, the API version) — passed per request
    /// rather than set on the shared <c>HttpClient</c>, because that client is also used by EODHD,
    /// BlackRock and Wikipedia, none of which should ever see an Anthropic credential on the wire.</param>
    Task<string> PostStringAsync(
        string url, string body, string contentType, string source,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default);

    /// <summary>GET with per-request headers — the batch poll and results calls need the same auth as the
    /// POST that created the batch.</summary>
    Task<string> GetStringAsync(
        string url, string source, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct = default);
}

/// <summary>
/// Hand-rolled resilient HTTP wrapper (no Polly — decision #2). 30s timeout, N retries with
/// exponential backoff + jitter, and a consecutive-failure circuit breaker. The delay and jitter
/// sources are injectable so unit tests are deterministic and never actually sleep. Single-threaded
/// per provider during backfill, so the failure counter needs no locking.
/// </summary>
public sealed class ResilientHttpClient : IResilientHttpSender
{
    private readonly HttpClient _http;
    private readonly ResilientHttpOptions _opts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private int _consecutiveFailures;

    /// <summary>The <c>X-RateLimit-Limit</c> / <c>X-RateLimit-Remaining</c> last seen on a response
    /// (INTEGRATIONS §1), for observability. Null until a response carries the headers.</summary>
    public int? LastRateLimitLimit { get; private set; }
    public int? LastRateLimitRemaining { get; private set; }

    public ResilientHttpClient(
        HttpClient http,
        ResilientHttpOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null)
    {
        _http = http;
        _opts = options ?? new ResilientHttpOptions();
        _http.Timeout = _opts.Timeout;
        // A descriptive User-Agent is required by Wikimedia (header-less ⇒ 403; observed 2026-07-14). Set it
        // once here so every provider inherits it; respect a UA the caller already configured on the client.
        if (_opts.UserAgent is { Length: > 0 } && _http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(_opts.UserAgent);
        }
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    public Task<string> GetStringAsync(string url, string source, CancellationToken ct = default)
        => GetStringAsync(url, source, headers: null, ct);

    public Task<string> GetStringAsync(
        string url, string source, IReadOnlyDictionary<string, string>? headers, CancellationToken ct = default)
        => SendAsync(() => Build(HttpMethod.Get, url, headers, body: null, contentType: null), url, source, ct);

    public Task<string> PostStringAsync(
        string url, string body, string contentType, string source,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
        => SendAsync(() => Build(HttpMethod.Post, url, headers, body, contentType), url, source, ct);

    /// <summary>Builds a FRESH request per attempt — an <see cref="HttpRequestMessage"/> cannot be sent
    /// twice, so the retry loop takes a factory rather than an instance.</summary>
    private static HttpRequestMessage Build(
        HttpMethod method, string url, IReadOnlyDictionary<string, string>? headers,
        string? body, string? contentType)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType ?? "application/json");
        }
        if (headers is not null)
        {
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        }
        return req;
    }

    private async Task<string> SendAsync(
        Func<HttpRequestMessage> request, string url, string source, CancellationToken ct)
    {
        if (_consecutiveFailures >= _opts.CircuitBreakThreshold)
        {
            throw new CircuitOpenException(source, _consecutiveFailures);
        }

        Exception? last = null;
        for (var attempt = 0; attempt <= _opts.MaxRetries; attempt++)
        {
            try
            {
                using var req = request();
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _consecutiveFailures = 0; // success resets the breaker

                // Honour the 1,000/min limit (INTEGRATIONS §1): read remaining, and if it is running low
                // pause before returning so the caller's next request is spaced out. Distinct from the
                // daily-cap headroom check (api_usage_log) — that guards 100k/day, this guards the minute.
                LastRateLimitLimit = ReadHeaderInt(resp, "X-RateLimit-Limit");
                LastRateLimitRemaining = ReadHeaderInt(resp, "X-RateLimit-Remaining");
                var cooldown = RateLimitGuard.CooldownFor(LastRateLimitRemaining, _opts.RateLimitRemainingFloor, _opts.RateLimitCooldown);
                if (cooldown > TimeSpan.Zero) await _delay(cooldown, ct).ConfigureAwait(false);

                return body;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                last = ex;
                if (attempt < _opts.MaxRetries)
                {
                    var baseMs = _opts.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
                    var wait = TimeSpan.FromMilliseconds(baseMs + (_jitter() * baseMs));
                    await _delay(wait, ct).ConfigureAwait(false);
                }
            }
        }

        _consecutiveFailures++;
        throw new HttpFetchException(url, last!);
    }

    private static int? ReadHeaderInt(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values)
            && int.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
}
