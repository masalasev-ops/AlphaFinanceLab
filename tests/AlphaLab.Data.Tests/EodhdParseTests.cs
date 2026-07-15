using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Offline parse tests for the EODHD provider (FR-1), against REAL captured payloads (captured
/// 2026-07-13, response body only) in tests/Fixtures/eodhd. Confirms the parsers handle the actual
/// wire shapes documented in INTEGRATIONS §1 — including the cases that matter: adjusted_close
/// diverging from close on split/dividend-affected rows, index volume exceeding Int32, and the
/// split string-ratio form. Parse logic is separated from HTTP so these never touch the network.
/// </summary>
public class EodhdParseTests
{
    // /eod: raw OHLCV + adjusted_close only (INTEGRATIONS §1: "O/H/L are RAW ... there is NO adjusted OHLC").
    [Fact]
    public void ParseEod_RealAaplRecent_MapsRawOhlcvAndAdjustedClose()
    {
        var bars = EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_AAPL.json"));

        Assert.Equal(62, bars.Count);
        var b = bars[0]; // 2026-04-14 — adjusted_close differs from close (a dividend adjustment)
        Assert.Equal("2026-04-14", b.Date);
        Assert.Equal(259.25, b.Open);
        Assert.Equal(261.93, b.High);
        Assert.Equal(257.19, b.Low);
        Assert.Equal(258.83, b.Close);
        Assert.Equal(258.5917, b.AdjClose);
        Assert.Equal(48370700, b.Volume);
        Assert.NotEqual(b.Close, b.AdjClose); // real divergence, not the synthetic close==adj
    }

    [Fact]
    public void ParseEod_RealAaplAdjusted_AdjustedCloseDivergesStrongly_OnPreSplitRows()
    {
        var bars = EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_AAPL_adjusted.json"));

        Assert.Equal(757, bars.Count);
        var first = bars[0]; // 2019-01-02, before the Aug-2020 4:1 split ⇒ adj_close ≈ close/4 further adjusted for divs
        Assert.Equal("2019-01-02", first.Date);
        Assert.Equal(157.92, first.Close);
        Assert.Equal(37.4692, first.AdjClose);
        Assert.True(first.AdjClose < first.Close / 3); // dramatic divergence on a pre-split row
    }

    [Fact]
    public void ParseEod_RealGspcIndex_VolumeExceedsInt32_FitsLong()
    {
        var bars = EodhdMarketDataProvider.ParseEod(Fixtures.Eodhd("eod_GSPC_INDX.json"));

        Assert.Equal(62, bars.Count);
        var b = bars[0]; // 2026-04-14
        Assert.Equal("2026-04-14", b.Date);
        Assert.Equal(6967.3799, b.Close);
        Assert.Equal(5032380000, b.Volume);
        Assert.True(b.Volume > int.MaxValue); // proves long (not int) is required for index volume
    }

    [Fact]
    public void ParseEod_EmptyArray_YieldsNoBars()
    {
        Assert.Empty(EodhdMarketDataProvider.ParseEod("[]"));
    }

    // The split string-ratio rule is pure logic — keep exhaustive unit coverage independent of any capture.
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

    [Fact]
    public void ParseSplits_RealAapl_ParsesStringRatios_IncludingTheFourForOne()
    {
        var splits = EodhdMarketDataProvider.ParseSplits(Fixtures.Eodhd("splits_AAPL.json"));

        Assert.Equal(5, splits.Count);
        // The Aug-2020 4:1 split, in the real "new/old" string-ratio form.
        var fourForOne = Assert.Single(splits, s => s.Date == "2020-08-31");
        Assert.Equal("4.000000/1.000000", fourForOne.RawRatio);
        Assert.Equal(4.0, fourForOne.Ratio, precision: 9);
        // Every real ratio parses (no format drift).
        Assert.All(splits, s => Assert.True(s.Ratio > 0));
    }

    [Fact]
    public void ParseDividends_RealAapl_ExDateIsDate_WithAdjustedAndUnadjustedValues()
    {
        var divs = EodhdMarketDataProvider.ParseDividends("AAPL", Fixtures.Eodhd("div_AAPL.json"));

        Assert.Equal(80, divs.Count);

        // Most recent (2026-05-11): value == unadjustedValue (no later split to adjust for).
        var recent = Assert.Single(divs, d => d.Date == "2026-05-11");
        Assert.Equal(0.27m, recent.Value);
        Assert.Equal(0.27m, recent.UnadjustedValue);

        // Oldest (1990-02-16): the retro split-adjusted value diverges hard from the actual cash paid.
        var old = Assert.Single(divs, d => d.Date == "1990-02-16");
        Assert.Equal(0.00098m, old.Value);
        Assert.Equal(0.10976m, old.UnadjustedValue);
    }

    // A dividend row with no unadjustedValue must fail CLOSED (rule 10, P1R-1) rather than fall back to
    // the split-adjusted value — and the throw must name the symbol + ex-date so the operator can act.
    [Fact]
    public void ParseDividends_NullUnadjustedValue_FailsClosed_NamingSymbolAndExDate()
    {
        const string json = """[{"date":"2020-01-01","value":0.25}]"""; // unadjustedValue absent -> null

        var ex = Assert.Throws<FormatException>(() => EodhdMarketDataProvider.ParseDividends("AAPL", json));
        Assert.Contains("AAPL", ex.Message);
        Assert.Contains("2020-01-01", ex.Message);
    }
}
