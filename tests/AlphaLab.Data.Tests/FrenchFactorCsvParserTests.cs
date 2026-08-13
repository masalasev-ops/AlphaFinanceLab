using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The Ken French daily CSV parser (checkpoint 6.6, D41/FR-5). Every fixture here is a shape the real
/// files actually have — a prose preamble of unknown length, a header whose first cell is empty, an
/// ANNUAL table after the daily one, percent units, and `-99.99` for missing.
/// </summary>
public class FrenchFactorCsvParserTests
{
    // The 5-factor + RF daily file, trimmed. Note the second table: the real file carries it and reading
    // it as daily data is the defect the stop-rule prevents.
    private const string FiveFactorFile =
        "This file was created by CMPT_ME_BEME_RETS using the 202607 CRSP database.\r\n" +
        "The 1-month TBill return is from Ibbotson and Associates Inc.\r\n" +
        "\r\n" +
        ",Mkt-RF,SMB,HML,RMW,CMA,RF\r\n" +
        "20260701,-0.67, 0.02,-0.35, 0.03, 0.13,0.012\r\n" +
        "20260702, 0.79,-0.28, 0.28,-0.08,-0.21,0.012\r\n" +
        "20260703, 1.25, 0.11,-0.02, 0.15, 0.04,0.013\r\n" +
        "\r\n" +
        "Annual Factors: January-December\r\n" +
        ",Mkt-RF,SMB,HML,RMW,CMA,RF\r\n" +
        "2025,12.34,1.11,2.22,3.33,4.44,4.10\r\n";

    private const string MomentumFile =
        "This file was created by CMPT_ME_RETS using the 202607 CRSP database.\r\n" +
        "\r\n" +
        ",Mom   \r\n" +
        "20260701, 0.44\r\n" +
        "20260702,-1.05\r\n";

    [Fact]
    public void FR5_D41_ParsesTheDailyBlock_WithIsoDatesAndCanonicalTokens()
    {
        var obs = FrenchFactorCsvParser.Parse(FiveFactorFile);

        Assert.Equal(3, obs.Select(o => o.Date).Distinct().Count());
        Assert.Contains(obs, o => o is { Date: "2026-07-01", Factor: "MKT_RF" });
        Assert.Contains(obs, o => o is { Date: "2026-07-03", Factor: "CMA" });
        Assert.All(obs, o => Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", o.Date));
        Assert.All(obs, o => Assert.Contains(o.Factor, new[] { "MKT_RF", "SMB", "HML", "RMW", "CMA", "RF" }));
    }

    /// <summary>Percent → decimal at the boundary. −0.67 % must land as −0.0067, not −0.67; the symptom
    /// of getting this wrong is a β off by 100×, which does not look like a units bug.</summary>
    [Fact]
    public void FR5_D41_ConvertsPercentToDecimal_AtTheBoundary()
    {
        var obs = FrenchFactorCsvParser.Parse(FiveFactorFile);

        var mkt = obs.Single(o => o is { Date: "2026-07-01", Factor: "MKT_RF" });
        Assert.Equal(-0.0067, mkt.Value, 12);

        var rf = obs.Single(o => o is { Date: "2026-07-03", Factor: "RF" });
        Assert.Equal(0.00013, rf.Value, 12);
    }

    /// <summary>THE STOP RULE. The annual table repeats the same header and its "dates" are four-digit
    /// years, so without the eight-digit rule it would be read as daily data — storing a 12.34 % day
    /// under a date like `2025-…`. Nothing downstream would flag it.</summary>
    [Fact]
    public void FR5_D41_TheAnnualTable_IsNotReadAsDailyData()
    {
        var obs = FrenchFactorCsvParser.Parse(FiveFactorFile);

        Assert.All(obs, o => Assert.StartsWith("2026-07-", o.Date, StringComparison.Ordinal));
        Assert.DoesNotContain(obs, o => Math.Abs(o.Value) > 0.05);   // no annual-sized number leaked in
        Assert.Equal(18, obs.Count);                                  // 3 dates × 6 factors, and nothing more
    }

    [Fact]
    public void FR5_D41_MomFileMapsToUmd_TheOneRename()
    {
        var obs = FrenchFactorCsvParser.Parse(MomentumFile);

        Assert.All(obs, o => Assert.Equal("UMD", o.Factor));
        Assert.Equal(-0.0105, obs.Single(o => o.Date == "2026-07-02").Value, 12);
    }

    /// <summary>`-99.99` and `-999` are "no data", not returns. Storing them would put a −99 % day into
    /// a regression; dropping them leaves the date absent, where the continuity check can see it.</summary>
    [Theory]
    [InlineData("-99.99")]
    [InlineData("-999")]
    public void FR5_D41_MissingSentinels_AreDropped_NotStoredAsReturns(string sentinel)
    {
        var csv =
            "preamble\r\n\r\n" +
            ",Mkt-RF,RF\r\n" +
            $"20260701,{sentinel},0.012\r\n" +
            "20260702,0.50,0.012\r\n";

        var obs = FrenchFactorCsvParser.Parse(csv);

        Assert.DoesNotContain(obs, o => o is { Date: "2026-07-01", Factor: "MKT_RF" });
        Assert.Contains(obs, o => o is { Date: "2026-07-01", Factor: "RF" });      // the row survives
        Assert.Contains(obs, o => o is { Date: "2026-07-02", Factor: "MKT_RF" });
        Assert.DoesNotContain(obs, o => o.Value < -0.9);
    }

    /// <summary>The preamble has no fixed length, so the header is found rather than counted.</summary>
    [Fact]
    public void FR5_D41_APreambleOfAnyLength_DoesNotShiftTheHeader()
    {
        var longPreamble = string.Concat(Enumerable.Repeat("some prose line\r\n", 40));
        var obs = FrenchFactorCsvParser.Parse(longPreamble + "\r\n,Mkt-RF,RF\r\n20260701,0.50,0.012\r\n");

        Assert.Equal(2, obs.Count);
    }

    // ---------- fail loud ----------

    /// <summary>An HTML error page decodes to text without throwing, so "no header" is the realistic
    /// wrong-payload case and it must refuse rather than report zero rows refreshed.</summary>
    [Fact]
    public void FR5_D41_APayloadWithNoFactorHeader_IsRefused_NotSilentlyEmpty()
    {
        var ex = Assert.Throws<FrenchFactorFormatException>(
            () => FrenchFactorCsvParser.Parse("<html><body>404 Not Found</body></html>"));
        Assert.Contains("No factor header row found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR5_D41_AHeaderWithNoDataRows_IsRefused()
    {
        var ex = Assert.Throws<FrenchFactorFormatException>(
            () => FrenchFactorCsvParser.Parse("preamble\r\n\r\n,Mkt-RF,RF\r\n\r\nAnnual Factors:\r\n"));
        Assert.Contains("no YYYYMMDD data row", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A cell that does not parse means the layout is not what the parser thinks it is, so the
    /// remaining columns cannot be trusted either — refuse the file, do not skip the cell.</summary>
    [Fact]
    public void FR5_D41_AnUnparseableValue_RefusesTheWholeFile_RatherThanSkippingTheCell()
    {
        var ex = Assert.Throws<FrenchFactorFormatException>(
            () => FrenchFactorCsvParser.Parse("p\r\n\r\n,Mkt-RF,RF\r\n20260701,N/A,0.012\r\n"));
        Assert.Contains("Unparseable value", ex.Message, StringComparison.Ordinal);
    }
}
