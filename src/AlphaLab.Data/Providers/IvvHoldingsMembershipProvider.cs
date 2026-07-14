using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>
/// Endpoint config for the iShares holdings CSV feeds (INTEGRATIONS §2/§2b). The BlackRock
/// get-fund-document endpoint serves the CSV when <c>component=holdings</c> and <c>asOfDate</c> is
/// OMITTED (a pinned asOfDate freezes the download to a stale day). PortfolioId selects the fund:
/// 239726 = IVV (S&amp;P 500), 239723 = OEF (S&amp;P 100 slice).
/// </summary>
public sealed class ISharesHoldingsOptions
{
    public string BaseUrl { get; init; } =
        "https://www.blackrock.com/varnish-api/blk-one01-product-data/product-data/api/v1/get-fund-document";
    /// <summary>239726 = IVV (S&amp;P 500). OEF (239723) reuses this provider in 1.5.</summary>
    public string PortfolioId { get; init; } = "239726";
    /// <summary>Config source label written to index_membership_log / the raw cache (e.g. "ivv_csv").</summary>
    public string Source { get; init; } = "ivv_csv";
}

/// <summary>
/// PRIMARY membership provider (D49): the iShares IVV holdings CSV (S&amp;P 500). Fetches the
/// <c>component=holdings</c> CSV WITHOUT asOfDate, archives the raw payload, parses via
/// <see cref="ISharesHoldingsParser"/>, and canonicalizes each ticker to the EODHD form. One CSV
/// shape serves OEF too — the S&amp;P 100 slice provider (1.5) reuses this class with the OEF
/// PortfolioId + source label. Fetch and parse are split so <see cref="ToSnapshot"/> is unit-tested
/// offline against the byte-real fixture.
/// </summary>
public sealed class IvvHoldingsMembershipProvider(
    IResilientHttpClient http,
    ISharesHoldingsOptions options,
    IRawCache? rawCache = null) : IIndexMembershipProvider
{
    private readonly IRawCache _rawCache = rawCache ?? NullRawCache.Instance;

    public async Task<MembershipSnapshot> GetMembersAsync(CancellationToken ct = default)
    {
        var url =
            $"{options.BaseUrl}?appType=PRODUCT_PAGE&appSubType=ISHARES&targetSite=us-ishares" +
            $"&locale=en_US&portfolioId={options.PortfolioId}&userType=individual&component=holdings";
        var csv = await http.GetStringAsync(url, options.Source, ct).ConfigureAwait(false);
        _rawCache.Save(options.Source, "latest", $"{options.PortfolioId}.holdings.csv", csv);
        return ToSnapshot(options.Source, csv);
    }

    /// <summary>Pure: parse the holdings CSV and canonicalize to EODHD symbols. Unit-tested offline.</summary>
    public static MembershipSnapshot ToSnapshot(string source, string csv)
    {
        var holdings = ISharesHoldingsParser.ParseHoldings(csv);
        var members = new List<MemberRow>(holdings.Count);
        foreach (var h in holdings)
        {
            members.Add(new MemberRow(SymbolNormalizer.ToEodhd(h.Ticker), h.Ticker, h.Sector));
        }
        return new MembershipSnapshot(source, members);
    }
}
