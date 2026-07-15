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
/// Hand-rolled resilient HTTP wrapper (no Polly — decision #2). 30s timeout, N retries with
/// exponential backoff + jitter, and a consecutive-failure circuit breaker. The delay and jitter
/// sources are injectable so unit tests are deterministic and never actually sleep. Single-threaded
/// per provider during backfill, so the failure counter needs no locking.
/// </summary>
public sealed class ResilientHttpClient : IResilientHttpClient
{
    private readonly HttpClient _http;
    private readonly ResilientHttpOptions _opts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private int _consecutiveFailures;

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

    public async Task<string> GetStringAsync(string url, string source, CancellationToken ct = default)
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
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _consecutiveFailures = 0; // success resets the breaker
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
}
