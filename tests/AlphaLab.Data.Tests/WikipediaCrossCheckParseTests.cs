using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Offline parse tests for the Wikipedia membership cross-check (FR-4 / D49), against the re-saved
/// byte-real rendered pages in tests/Fixtures. One extractor handles both page shapes: the S&amp;P 500
/// page carries tickers as &lt;a&gt; links (NyseSymbol/NasdaqSymbol templates); the S&amp;P 100 page
/// uses plain-text cells. The header guard fails loud on markup drift rather than returning an empty set.
/// </summary>
public class WikipediaCrossCheckParseTests
{
    [Fact]
    public void ToSnapshot_RealSp500Page_ExtractsConstituentsInBand_WithKnownMembers()
    {
        var snap = WikipediaMembershipCrossCheck.ToSnapshot(
            "wikipedia", Fixtures.Wikipedia("sp500_constituents.html"));

        Assert.InRange(snap.Members.Count, 495, 510);

        var canonical = snap.Members.Select(m => m.CanonicalSymbol).ToHashSet();
        Assert.Contains("AAPL", canonical);
        Assert.Contains("MSFT", canonical);
        Assert.Contains("BRK-B", canonical); // dot BRK.B canonicalized to dash

        // The raw side keeps Wikipedia's dot spelling.
        Assert.Contains(snap.Members, m => m.RawSymbol == "BRK.B");

        // No HTML leaked into any symbol.
        Assert.All(snap.Members, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.CanonicalSymbol));
            Assert.DoesNotContain('<', m.CanonicalSymbol);
            Assert.DoesNotContain(' ', m.CanonicalSymbol);
        });
    }

    [Fact]
    public void ToSnapshot_RealSp100Page_PlainTextCells_ParseInBand()
    {
        var snap = WikipediaMembershipCrossCheck.ToSnapshot(
            "wikipedia_sp100", Fixtures.Wikipedia("sp100_components.html"));

        Assert.InRange(snap.Members.Count, 99, 103);
        var canonical = snap.Members.Select(m => m.CanonicalSymbol).ToHashSet();
        Assert.Contains("AAPL", canonical);
        Assert.Contains("BRK-B", canonical);
    }

    [Fact]
    public void Extract_HeaderDrift_FailsLoud()
    {
        // First column header is not "Symbol" ⇒ guard throws (never a silent empty set).
        const string html =
            "<table id=\"constituents\"><tbody>" +
            "<tr><th>Ticker</th><th>Security</th></tr>" +
            "<tr><td>AAPL</td><td>Apple</td></tr>" +
            "</tbody></table>";
        Assert.Throws<FormatException>(() => WikitableExtractor.ExtractConstituentsTable(html));
    }

    [Fact]
    public void Extract_TableNotFound_FailsLoud()
    {
        Assert.Throws<FormatException>(() =>
            WikitableExtractor.ExtractConstituentsTable("<p>no constituents table here</p>"));
    }

    [Fact]
    public void Extract_MinimalWellFormedTable_ReadsFirstColumn()
    {
        const string html =
            "<table id=\"constituents\"><tbody>" +
            "<tr><th>Symbol</th><th>Security</th></tr>" +
            "<tr><td><a href=\"x\">MMM</a></td><td>3M</td></tr>" +
            "<tr><td>BRK.B</td><td>Berkshire</td></tr>" +
            "</tbody></table>";
        var tickers = WikitableExtractor.ExtractConstituentsTable(html);
        Assert.Equal(new[] { "MMM", "BRK.B" }, tickers);
    }
}
