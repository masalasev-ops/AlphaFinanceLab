using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>Config for the Wikipedia membership cross-check (INTEGRATIONS §7). The constituents
/// wikitable on the S&amp;P 500 / S&amp;P 100 list page is the daily cross-check against the IVV/OEF
/// primary (D49).</summary>
public sealed class WikipediaMembershipOptions
{
    public string Url { get; init; } = "https://en.wikipedia.org/wiki/List_of_S%26P_500_companies";
    public string Source { get; init; } = "wikipedia";
}

/// <summary>
/// Wikipedia membership cross-check (D49; INTEGRATIONS §7). Fetches the list page, archives the raw
/// HTML, and extracts the constituents-table tickers via <see cref="WikitableExtractor"/>, each
/// canonicalized to the EODHD symbol (dot→dash). Fetch and parse are split so <see cref="ToSnapshot"/>
/// is unit-tested offline against the byte-real page fixture.
/// </summary>
public sealed class WikipediaMembershipCrossCheck(
    IResilientHttpClient http,
    WikipediaMembershipOptions options,
    IRawCache? rawCache = null) : IIndexMembershipProvider
{
    private readonly IRawCache _rawCache = rawCache ?? NullRawCache.Instance;

    public async Task<MembershipSnapshot> GetMembersAsync(string asOf, CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(options.Url, options.Source, ct).ConfigureAwait(false);
        _rawCache.Save(options.Source, asOf, "constituents.html", html); // observation day, not "latest" (dated roster provenance)
        return ToSnapshot(options.Source, html);
    }

    /// <summary>Pure: extract constituents tickers from the page HTML and canonicalize to EODHD
    /// symbols. Sector is not read from Wikipedia in 1.4 (the IVV/OEF primary is the sector source).</summary>
    public static MembershipSnapshot ToSnapshot(string source, string html)
    {
        var tickers = WikitableExtractor.ExtractConstituentsTable(html);
        var members = new List<MemberRow>(tickers.Count);
        foreach (var raw in tickers)
        {
            members.Add(new MemberRow(SymbolNormalizer.ToEodhd(raw), raw, null));
        }
        return new MembershipSnapshot(source, members);
    }
}
