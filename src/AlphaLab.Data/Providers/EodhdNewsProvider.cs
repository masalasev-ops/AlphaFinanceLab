using System.Text.Json;
using AlphaLab.Core.Llm;
using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>
/// The raw EODHD news fetch (D35/D46; INTEGRATIONS §1).
///
/// **This is the UNBUDGETED feed.** It implements <see cref="INewsProvider"/> so the budget can decorate
/// it, but nothing outside composition should hold a reference to it: the D46 rail is
/// <c>BudgetedNewsProvider</c>, and reaching past it is exactly the bypass the decorator shape exists to
/// prevent. Named `Raw` in composition for that reason.
///
/// `/news` costs **5** units against the EODHD daily cap (INTEGRATIONS §1) — five times a bar fetch — so
/// it is a market-level read once per day at ScopeLevel 1, never a per-symbol sweep.
/// </summary>
public sealed class EodhdNewsProvider(
    IResilientHttpClient http,
    EodhdOptions options,
    IApiUsageRecorder? usage = null,
    IRawCache? rawCache = null) : INewsProvider
{
    private const string Source = "eodhd";
    private readonly IRawCache _rawCache = rawCache ?? NullRawCache.Instance;

    /// <summary>Articles per fetch requested from the feed. Deliberately LARGER than the D46 cap: the
    /// budget's job is to choose which 25 are worth tokens, and it can only choose from what it was
    /// given. Fetching 25 would make the cap meaningless — the feed's arbitrary ordering would be the
    /// selection.</summary>
    public int FetchLimit { get; init; } = 200;

    public async Task<IReadOnlyList<NewsArticle>> GetAdmittedAsync(string asOf, CancellationToken ct = default)
    {
        var url = $"{options.BaseUrl}/news?api_token={options.ApiToken}&fmt=json&from={asOf}&to={asOf}&limit={FetchLimit}";
        var json = await http.GetStringAsync(url, Source, ct).ConfigureAwait(false);

        _rawCache.Save(Source, asOf, "news.json", json);
        usage?.Count(Source, EodhdEndpointCost.For(EodhdEndpoint.News));

        return Parse(json);
    }

    /// <summary>Pure parse of the documented shape (INTEGRATIONS §1, VERIFIED 2026-07-13): an array of
    /// <c>{date, title, content, link, symbols, tags, sentiment}</c>. <c>sentiment</c> is returned inline
    /// and is **deliberately ignored** — the machine-readable sentiment score is retired (D46 amended by
    /// D79–D82, golden rule 28); reading it back in would resurrect the thing that was retired.</summary>
    public static IReadOnlyList<NewsArticle> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var outp = new List<NewsArticle>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            outp.Add(new NewsArticle(
                Str(el, "title") ?? "",
                Str(el, "content") ?? "",
                Str(el, "link"),
                StrArray(el, "symbols"),
                StrArray(el, "tags")));
        }
        return outp;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> StrArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        var outp = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) outp.Add(s);
        }
        return outp;
    }
}

/// <summary>Seam for weighting a call into <c>api_usage_log</c> (INTEGRATIONS §1: per-endpoint cost, not
/// a flat count).</summary>
public interface IApiUsageRecorder
{
    void Count(string source, int units);
}
