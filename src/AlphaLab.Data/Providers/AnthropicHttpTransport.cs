using AlphaLab.Core.Llm;
using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>Anthropic endpoint + credential settings (INTEGRATIONS §5).</summary>
public sealed class AnthropicTransportOptions
{
    public string BaseUrl { get; init; } = "https://api.anthropic.com";

    /// <summary>From the gitignored <c>appsettings.Secrets.json</c> (<c>Secrets:AnthropicApiKey</c>, D67).
    /// Never logged, never echoed, never in the committed appsettings.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>The <c>anthropic-version</c> header (INTEGRATIONS §5).</summary>
    public string ApiVersion { get; init; } = "2023-06-01";

    /// <summary>Source name for the circuit breaker and <c>api_usage_log</c>.</summary>
    public string SourceName { get; init; } = "anthropic";
}

/// <summary>
/// Satisfies the Core <see cref="IModelTransport"/> port over the shared
/// <see cref="IResilientHttpClient"/>.
///
/// **This class is the whole reason the port exists.** `ci.ps1` asserts <c>AlphaLab.Llm = (Core)</c>, so
/// the LLM layer cannot reach the resilient client, which lives here. Rather than give AlphaLab.Llm a
/// second HTTP stack — duplicating the retry, backoff and circuit-breaker policy, and quietly
/// contradicting INTEGRATIONS §9 — the LLM layer states its need as a port and this adapter satisfies it.
/// The resilience policy therefore stays in exactly one place for every provider in the lab.
///
/// The API key is attached **per request** rather than to the shared <c>HttpClient</c>, because that
/// client is also used by EODHD, BlackRock and Wikipedia — none of which should ever see an Anthropic
/// credential on the wire.
/// </summary>
public sealed class AnthropicHttpTransport(IResilientHttpSender http, AnthropicTransportOptions options)
    : IModelTransport
{
    private Dictionary<string, string> Headers() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-api-key"] = options.ApiKey,
        ["anthropic-version"] = options.ApiVersion,
    };

    private string Url(string path) => options.BaseUrl.TrimEnd('/') + path;

    public async Task<string> PostJsonAsync(string path, string jsonBody, CancellationToken ct = default)
    {
        try
        {
            return await http
                .PostStringAsync(Url(path), jsonBody, "application/json", options.SourceName, Headers(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<string> GetJsonAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await http.GetStringAsync(Url(path), options.SourceName, Headers(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>Translate transport failures into the Core exception the provider understands, carrying
    /// the HTTP status where one exists so a 4xx (a bad request — retrying wastes budget) is
    /// distinguishable from a 429/5xx without string-matching a message.</summary>
    private static ModelTransportException Translate(Exception ex) => ex switch
    {
        HttpFetchException { InnerException: HttpRequestException { StatusCode: { } sc } inner } fetch
            => new ModelTransportException((int)sc, fetch.Message, inner),
        HttpFetchException fetch => new ModelTransportException(null, fetch.Message, fetch),
        CircuitOpenException open => new ModelTransportException(null, open.Message, open),
        _ => new ModelTransportException(null, ex.Message, ex),
    };
}
