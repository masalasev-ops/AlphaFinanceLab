namespace AlphaLab.Core.Llm;

/// <summary>Thrown when the model API answers with a non-success status after retries. Carries the status
/// so the provider can distinguish "retry later" (429/5xx) from "this request is wrong" (4xx) without
/// string-matching a message.</summary>
public sealed class ModelTransportException(int? statusCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>HTTP status, or null when the call never got one (network/timeout/circuit-open).</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>Retryable per INTEGRATIONS §9 + the API's own guidance: 408/409/429 and 5xx, plus the
    /// no-status case (a connection failure). A 4xx is a bad request and retrying it wastes budget.</summary>
    public bool IsRetryable => StatusCode is null or 408 or 409 or 429 or (>= 500 and < 600);
}

/// <summary>
/// The narrow port the LLM layer talks to instead of an HTTP client.
///
/// **WHY THIS EXISTS — it is forced by the reference graph, not by taste.** `ci.ps1` asserts
/// <c>AlphaLab.Llm = (Core)</c>, so the LLM layer **cannot reference AlphaLab.Data**, where the shared
/// resilient client (30 s timeout, 3 retries with exponential backoff + jitter, circuit-break at 5 —
/// INTEGRATIONS §9) lives. That client is also **GET-only**, and the Batches API needs POST. So the
/// recorded position in INTEGRATIONS §5 — that the Anthropic client "inherits" the shared resilient
/// client — was **not reachable as the graph stood** (finding 323).
///
/// Three ways out were available. Giving AlphaLab.Llm its own HTTP stack duplicates the retry, backoff and
/// breaker logic and contradicts INTEGRATIONS §9's "every provider goes through a shared resilient client".
/// Relocating <c>IResilientHttpClient</c> into Core drags an HTTP-shaped abstraction, its options and its
/// rate-limit header handling into the domain project for one consumer. **This port is the third: the LLM
/// layer states what it needs — send a JSON body, get a JSON body — and AlphaLab.Data satisfies it over the
/// existing shared client**, which keeps the resilience policy in exactly one place and leaves AlphaLab.Llm
/// with no transport concerns at all.
///
/// The second payoff is testability, and it is not incidental: TEST_PLAN §6 requires a **mocked provider
/// for CI** with the live smoke test gated behind a trait. A fake transport gives the entire prompt-layering,
/// batching, cost and refusal path unit tests with no HTTP anywhere.
/// </summary>
public interface IModelTransport
{
    /// <summary>POST a JSON body to a path relative to the API base, returning the raw JSON response.
    /// Throws <see cref="ModelTransportException"/> on a non-success status after retries.</summary>
    Task<string> PostJsonAsync(string path, string jsonBody, CancellationToken ct = default);

    /// <summary>GET a JSON body from a path relative to the API base (batch poll, batch results).</summary>
    Task<string> GetJsonAsync(string path, CancellationToken ct = default);
}
