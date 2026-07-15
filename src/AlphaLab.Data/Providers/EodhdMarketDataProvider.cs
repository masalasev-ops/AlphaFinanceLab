using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>Endpoint config for EODHD (INTEGRATIONS §1). The token comes from the gitignored
/// appsettings.Secrets.json (Secrets:EodhdApiToken, D67) — never logged, never committed.</summary>
public sealed class EodhdOptions
{
    public string BaseUrl { get; init; } = "https://eodhd.com/api";
    public string ApiToken { get; init; } = default!;
    /// <summary>US exchange suffix appended to bare tickers for the /eod, /div, /splits calls.</summary>
    public string ExchangeSuffix { get; init; } = "US";
}

/// <summary>
/// EODHD market-data provider (D35/FR-1). Fetch goes through the resilient client and archives the
/// raw payload; the static Parse* helpers are pure so the response shapes (INTEGRATIONS §1) are
/// unit-tested offline against captured payloads. Live fetching is exercised by the backfill CLI
/// (decision #1). Store raw OHLCV + adjusted_close only — EODHD has no adjusted OHL, so
/// adj_open/adj_high/adj_low are never derived here (the factor adjusted_close/close is derivable
/// on read if ever needed).
/// </summary>
public sealed class EodhdMarketDataProvider(
    IResilientHttpClient http,
    EodhdOptions options,
    IRawCache? rawCache = null) : IMarketDataProvider
{
    private const string Source = "eodhd";
    private readonly IRawCache _rawCache = rawCache ?? NullRawCache.Instance;

    private string Sym(string symbol) => $"{symbol}.{options.ExchangeSuffix}";

    public async Task<IReadOnlyList<EodBar>> GetEodAsync(string symbol, string from, string to, CancellationToken ct = default)
    {
        var url = $"{options.BaseUrl}/eod/{Sym(symbol)}?api_token={options.ApiToken}&fmt=json&from={from}&to={to}&period=d";
        var json = await http.GetStringAsync(url, Source, ct).ConfigureAwait(false);
        _rawCache.Save(Source, to, $"{symbol}.eod.json", json);
        return ParseEod(json);
    }

    public async Task<IReadOnlyList<DividendEvent>> GetDividendsAsync(string symbol, string from, CancellationToken ct = default)
    {
        var url = $"{options.BaseUrl}/div/{Sym(symbol)}?api_token={options.ApiToken}&fmt=json&from={from}";
        var json = await http.GetStringAsync(url, Source, ct).ConfigureAwait(false);
        _rawCache.Save(Source, from, $"{symbol}.div.json", json);
        return ParseDividends(symbol, json);
    }

    public async Task<IReadOnlyList<SplitEvent>> GetSplitsAsync(string symbol, string from, CancellationToken ct = default)
    {
        var url = $"{options.BaseUrl}/splits/{Sym(symbol)}?api_token={options.ApiToken}&fmt=json&from={from}";
        var json = await http.GetStringAsync(url, Source, ct).ConfigureAwait(false);
        _rawCache.Save(Source, from, $"{symbol}.splits.json", json);
        return ParseSplits(json);
    }

    // ---- Pure parse helpers (unit-tested against captured payloads; INTEGRATIONS §1 shapes) ----

    /// <summary>Parse the /eod array: {date, open, high, low, close, adjusted_close, volume}. Raw
    /// OHLCV + adjusted_close only.</summary>
    public static IReadOnlyList<EodBar> ParseEod(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<EodDto>>(json) ?? [];
        var bars = new List<EodBar>(dtos.Count);
        foreach (var d in dtos)
        {
            if (string.IsNullOrWhiteSpace(d.Date)) continue;
            bars.Add(new EodBar(d.Date, d.Open, d.High, d.Low, d.Close, d.AdjustedClose, d.Volume));
        }
        return bars;
    }

    /// <summary>Parse the /div array: ex-date = date; value (adjusted) + unadjustedValue. A row whose
    /// <c>unadjustedValue</c> is null fails CLOSED (rule 10): the unadjusted amount is the actual cash a
    /// holder received (D69), and there is no safe substitute — falling back to the split-adjusted
    /// <c>value</c> would be wrong by the cumulative split factor (112× on the oldest AAPL fixture row).
    /// Mirrors <see cref="ParseSplitRatio"/>'s fail-loudly-on-drift precedent; <paramref name="symbol"/>
    /// is threaded in from the caller so the throw names the affected security + ex-date.</summary>
    public static IReadOnlyList<DividendEvent> ParseDividends(string symbol, string json)
    {
        var dtos = JsonSerializer.Deserialize<List<DivDto>>(json) ?? [];
        var events = new List<DividendEvent>(dtos.Count);
        foreach (var d in dtos)
        {
            if (string.IsNullOrWhiteSpace(d.Date)) continue;
            if (d.UnadjustedValue is null)
            {
                throw new FormatException(
                    $"Dividend for {symbol} ex-date {d.Date}: unadjustedValue is null (the actual cash " +
                    "a holder received is unknown) - refusing to fall back to the split-adjusted value.");
            }
            events.Add(new DividendEvent(d.Date, d.Value, d.UnadjustedValue));
        }
        return events;
    }

    /// <summary>Parse the /splits array: {date, split} where split is a string ratio
    /// "4.000000/1.000000" — parse on '/', never Convert the whole field (INTEGRATIONS §1).</summary>
    public static IReadOnlyList<SplitEvent> ParseSplits(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<SplitDto>>(json) ?? [];
        var events = new List<SplitEvent>(dtos.Count);
        foreach (var d in dtos)
        {
            if (string.IsNullOrWhiteSpace(d.Date) || string.IsNullOrWhiteSpace(d.Split)) continue;
            events.Add(new SplitEvent(d.Date, ParseSplitRatio(d.Split), d.Split));
        }
        return events;
    }

    /// <summary>"new/old" ⇒ new÷old (e.g. "4.000000/1.000000" ⇒ 4.0). Throws on a malformed field
    /// so a format drift fails loudly rather than silently mispricing (fail closed).</summary>
    public static double ParseSplitRatio(string raw)
    {
        var parts = raw.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator == 0)
        {
            throw new FormatException($"Unparseable split ratio: '{raw}'.");
        }
        return numerator / denominator;
    }

    // EODHD wire DTOs (snake_case / camelCase names per INTEGRATIONS §1).
    private sealed record EodDto(
        [property: JsonPropertyName("date")] string Date,
        [property: JsonPropertyName("open")] double? Open,
        [property: JsonPropertyName("high")] double? High,
        [property: JsonPropertyName("low")] double? Low,
        [property: JsonPropertyName("close")] double? Close,
        [property: JsonPropertyName("adjusted_close")] double? AdjustedClose,
        [property: JsonPropertyName("volume")] long? Volume);

    private sealed record DivDto(
        [property: JsonPropertyName("date")] string Date,
        [property: JsonPropertyName("value")] decimal? Value,
        [property: JsonPropertyName("unadjustedValue")] decimal? UnadjustedValue);

    private sealed record SplitDto(
        [property: JsonPropertyName("date")] string Date,
        [property: JsonPropertyName("split")] string Split);
}
