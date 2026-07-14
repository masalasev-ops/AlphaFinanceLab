using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Unit coverage for the RFC-4180 reader (the gotcha-handler under the iShares holdings parser).
/// The load-bearing case is the multi-line quoted field — the iShares disclaimer footer is a single
/// quoted field spanning ten physical lines, which a naive line-splitter shatters.
/// </summary>
public class CsvReaderTests
{
    [Fact]
    public void Parse_QuotedFieldWithInFieldCommas_StaysOneField()
    {
        var rows = DelimitedCsvReader.Parse("\"1,234\",\"5,678\"\n");
        Assert.Single(rows);
        Assert.Equal(new[] { "1,234", "5,678" }, rows[0]);
    }

    [Fact]
    public void Parse_QuotedFieldWithEmbeddedNewlines_StaysOneField()
    {
        var csv = "A,B\n\"line one\nline two\nline three\"\n";
        var rows = DelimitedCsvReader.Parse(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "A", "B" }, rows[0]);
        Assert.Single(rows[1]); // the multi-line quoted field is ONE field, not three rows
        Assert.Equal("line one\nline two\nline three", rows[1][0]);
    }

    [Fact]
    public void Parse_EscapedDoubleQuotes_Unescape()
    {
        var rows = DelimitedCsvReader.Parse("\"she said \"\"hi\"\"\",x\n");
        Assert.Equal(new[] { "she said \"hi\"", "x" }, rows[0]);
    }

    [Fact]
    public void Parse_BlankLine_YieldsSingleEmptyFieldRow()
    {
        var rows = DelimitedCsvReader.Parse("a,b\n\nc,d\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "a", "b" }, rows[0]);
        Assert.Equal(new[] { "" }, rows[1]); // blank line — the data/footer boundary marker
        Assert.Equal(new[] { "c", "d" }, rows[2]);
    }

    [Fact]
    public void Parse_CrlfAndTrailingNewline_NoPhantomRecord()
    {
        var rows = DelimitedCsvReader.Parse("a,b\r\nc,d\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "c", "d" }, rows[1]);
    }
}
