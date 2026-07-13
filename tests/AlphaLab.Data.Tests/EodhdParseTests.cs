using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Offline parse tests for the EODHD provider (FR-1), against the field shapes VERIFIED 2026-07-13
/// in INTEGRATIONS §1. These payloads are shape-faithful to the documented responses; when the real
/// captured payloads are dropped into tests/Fixtures they become the loaded input here without any
/// parser change. Parse logic is separated from HTTP so these never touch the network.
/// </summary>
public class EodhdParseTests
{
    // /eod: raw OHLCV + adjusted_close only (INTEGRATIONS §1: "O/H/L are RAW ... there is NO adjusted OHLC").
    private const string EodJson = """
    [
      {"date":"2026-07-09","open":211.0,"high":213.5,"low":210.2,"close":212.4,"adjusted_close":212.4,"volume":41000000},
      {"date":"2026-07-10","open":212.5,"high":214.0,"low":211.8,"close":213.9,"adjusted_close":213.9,"volume":38500000}
    ]
    """;

    [Fact]
    public void ParseEod_MapsRawOhlcvAndAdjustedClose()
    {
        var bars = EodhdMarketDataProvider.ParseEod(EodJson);

        Assert.Equal(2, bars.Count);
        var b = bars[0];
        Assert.Equal("2026-07-09", b.Date);
        Assert.Equal(211.0, b.Open);
        Assert.Equal(213.5, b.High);
        Assert.Equal(210.2, b.Low);
        Assert.Equal(212.4, b.Close);
        Assert.Equal(212.4, b.AdjClose);
        Assert.Equal(41000000, b.Volume);
    }

    [Fact]
    public void ParseEod_EmptyArray_YieldsNoBars()
    {
        Assert.Empty(EodhdMarketDataProvider.ParseEod("[]"));
    }

    [Theory]
    [InlineData("4.000000/1.000000", 4.0)]
    [InlineData("1.000000/4.000000", 0.25)] // reverse split
    [InlineData("3.000000/2.000000", 1.5)]
    public void ParseSplitRatio_SplitsOnSlash_NeverConvertsWholeField(string raw, double expected)
    {
        Assert.Equal(expected, EodhdMarketDataProvider.ParseSplitRatio(raw), precision: 9);
    }

    [Theory]
    [InlineData("4.000000")]        // no slash
    [InlineData("4.0/0.0")]         // zero denominator
    [InlineData("a/b")]             // non-numeric
    [InlineData("1/2/3")]           // too many parts
    public void ParseSplitRatio_FailsLoudly_OnMalformedField(string raw)
    {
        Assert.Throws<FormatException>(() => EodhdMarketDataProvider.ParseSplitRatio(raw));
    }

    // /splits: array of {date, split} where split is the "new/old" STRING ratio.
    private const string SplitsJson = """
    [
      {"date":"2020-08-31","split":"4.000000/1.000000"}
    ]
    """;

    [Fact]
    public void ParseSplits_ParsesStringRatioAndKeepsRaw()
    {
        var splits = EodhdMarketDataProvider.ParseSplits(SplitsJson);
        var s = Assert.Single(splits);
        Assert.Equal("2020-08-31", s.Date);
        Assert.Equal(4.0, s.Ratio, precision: 9);
        Assert.Equal("4.000000/1.000000", s.RawRatio);
    }

    // /div: ex-date = date; both adjusted (value) and unadjustedValue supplied.
    private const string DivJson = """
    [
      {"date":"2026-05-09","declarationDate":"2026-05-01","recordDate":"2026-05-12","paymentDate":"2026-05-15","period":"Quarterly","value":0.26,"unadjustedValue":0.26,"currency":"USD"}
    ]
    """;

    [Fact]
    public void ParseDividends_ExDateIsDate_WithAdjustedAndUnadjustedValues()
    {
        var divs = EodhdMarketDataProvider.ParseDividends(DivJson);
        var d = Assert.Single(divs);
        Assert.Equal("2026-05-09", d.Date);
        Assert.Equal(0.26m, d.Value);
        Assert.Equal(0.26m, d.UnadjustedValue);
    }
}
