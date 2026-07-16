using AlphaLab.Data.Http;

namespace AlphaLab.Data.Providers;

/// <summary>
/// Endpoint config for the iShares holdings CSV feeds (INTEGRATIONS §2/§2b). The BlackRock
/// get-fund-document endpoint serves the CSV when <c>component=holdings</c> and <c>asOfDate</c> is
/// OMITTED (a pinned asOfDate freezes the download to a stale day). <see cref="PortfolioId"/> selects
/// the fund: 239726 = IVV (S&amp;P 500), 239723 = OEF (S&amp;P 100 slice). The two feeds are one CSV
/// shape differing only by portfolioId + <see cref="Source"/> label — use the <see cref="Ivv"/> /
/// <see cref="Oef"/> presets (`Universe.MembershipPrimary` vs `Universe.Bootstrap.MembershipPrimary`).
/// </summary>
public sealed class ISharesHoldingsOptions
{
    public string BaseUrl { get; init; } =
        "https://www.blackrock.com/varnish-api/blk-one01-product-data/product-data/api/v1/get-fund-document";
    public string PortfolioId { get; init; } = "239726";
    /// <summary>Config source label written to index_membership_log / the raw cache.</summary>
    public string Source { get; init; } = "ivv_csv";

    /// <summary>IVV = S&amp;P 500 (D49 forward primary; the 1.4 default).</summary>
    public static ISharesHoldingsOptions Ivv() => new();

    /// <summary>OEF = the D70 S&amp;P 100 launch slice (Universe.Bootstrap.MembershipPrimary).</summary>
    public static ISharesHoldingsOptions Oef() => new() { PortfolioId = "239723", Source = "oef_csv" };
}

/// <summary>
/// iShares holdings membership provider (FR-4 / D49/D70). ONE class serves both named feeds — IVV
/// (S&amp;P 500, the forward primary) and OEF (the D70 S&amp;P 100 launch slice) — selected by
/// <see cref="ISharesHoldingsOptions"/>; the named-source distinction (Golden Rule 25) lives in the
/// config source label, and one C-4 header fixture covers both (INTEGRATIONS §2b). Fetches
/// <c>component=holdings</c> WITHOUT asOfDate, archives the raw payload, parses via
/// <see cref="ISharesHoldingsParser"/>, and canonicalizes each ticker to the EODHD symbol. Fetch and
/// parse are split so <see cref="ToSnapshot"/> is unit-tested offline against the byte-real fixtures.
/// </summary>
public sealed class ISharesHoldingsMembershipProvider(
    IResilientHttpClient http,
    ISharesHoldingsOptions options,
    IRawCache? rawCache = null) : IIndexMembershipProvider
{
    private readonly IRawCache _rawCache = rawCache ?? NullRawCache.Instance;

    public async Task<MembershipSnapshot> GetMembersAsync(string asOf, CancellationToken ct = default)
    {
        var url =
            $"{options.BaseUrl}?appType=PRODUCT_PAGE&appSubType=ISHARES&targetSite=us-ishares" +
            $"&locale=en_US&portfolioId={options.PortfolioId}&userType=individual&component=holdings";
        var csv = await http.GetStringAsync(url, options.Source, ct).ConfigureAwait(false);
        _rawCache.Save(options.Source, asOf, $"{options.PortfolioId}.holdings.csv", csv); // observation day, not "latest" (dated roster provenance)
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
