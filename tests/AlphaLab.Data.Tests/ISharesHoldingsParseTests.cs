using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Offline parse tests for the iShares holdings CSV (FR-4), against the REAL byte-real downloads in
/// tests/Fixtures (IVV_holdings.csv, OEF_holdings.csv). Covers the C-4 header guard (scan + verbatim
/// match; drift fails loud), the equity filter, the multi-line disclaimer footer, and the one-shape
/// reuse across IVV and OEF. Parse logic is separated from HTTP so these never touch the network.
/// </summary>
public class ISharesHoldingsParseTests
{
    [Fact]
    public void ParseHoldings_RealIvv_HeaderScanned_And504Equities()
    {
        var holdings = ISharesHoldingsParser.ParseHoldings(Fixtures.Holdings("IVV_holdings.csv"));
        Assert.Equal(504, holdings.Count);
        Assert.All(holdings, h => Assert.Equal("Equity", h.AssetClass));
    }

    [Fact]
    public void ParseHoldings_RealIvv_ExcludesTheFourNonEquityRows()
    {
        var tickers = ISharesHoldingsParser.ParseHoldings(Fixtures.Holdings("IVV_holdings.csv"))
            .Select(h => h.Ticker).ToHashSet();
        Assert.DoesNotContain("XTSLA", tickers); // money market
        Assert.DoesNotContain("USD", tickers);   // cash
        Assert.DoesNotContain("SGAFT", tickers); // cash collateral
        Assert.DoesNotContain("ESU6", tickers);  // futures (Market Value 0.00)
    }

    [Fact]
    public void ParseHoldings_RealIvv_ExtractsTickerAndSector_IncludingClassShare()
    {
        var holdings = ISharesHoldingsParser.ParseHoldings(Fixtures.Holdings("IVV_holdings.csv"));
        var nvda = Assert.Single(holdings, h => h.Ticker == "NVDA");
        Assert.Equal("Information Technology", nvda.Sector);
        Assert.Contains(holdings, h => h.Ticker == "AAPL");
        Assert.Contains(holdings, h => h.Ticker == "BRKB"); // the no-separator class-share holdings form
    }

    [Fact]
    public void ParseHoldings_RealIvv_MultiLineDisclaimerFooter_NotParsedAsData()
    {
        var holdings = ISharesHoldingsParser.ParseHoldings(Fixtures.Holdings("IVV_holdings.csv"));
        // The 10-line disclaimer never leaks in: every ticker is a short symbol.
        Assert.All(holdings, h => Assert.InRange(h.Ticker.Length, 1, 6));
    }

    [Fact]
    public void ParseHoldings_RealOef_SameShape_102Equities()
    {
        var holdings = ISharesHoldingsParser.ParseHoldings(Fixtures.Holdings("OEF_holdings.csv"));
        Assert.Equal(102, holdings.Count);
        Assert.Contains(holdings, h => h.Ticker == "NVDA");
    }

    [Fact]
    public void ParseHoldings_DriftedHeader_FailsLoud_C4()
    {
        // Synthesize a renamed column IN CODE (do NOT regenerate the byte-real fixture): Sector → GICS Sector.
        var drifted = Fixtures.Holdings("IVV_holdings.csv")
            .Replace(ISharesHoldingsParser.ExpectedHeader,
                     ISharesHoldingsParser.ExpectedHeader.Replace("Sector", "GICS Sector"));
        Assert.Throws<FormatException>(() => ISharesHoldingsParser.ParseHoldings(drifted));
    }
}
