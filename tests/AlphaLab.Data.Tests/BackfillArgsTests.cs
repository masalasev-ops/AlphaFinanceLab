using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>Command-line parsing for the bootstrap CLI (1.10). Unknown/incomplete flags fail closed.</summary>
public class BackfillArgsTests
{
    private const string Today = "2026-07-13";

    [Fact]
    public void Parse_FullArgs()
    {
        var o = BackfillArgs.Parse(["--universe", "sp100", "--as-of", "2025-01-02", "--years", "10", "--dry-run"], Today);
        Assert.Equal("sp100", o.Universe);
        Assert.Equal("2025-01-02", o.AsOf);
        Assert.Equal(10, o.BackfillYears);
        Assert.True(o.DryRun);
        Assert.Equal([99, 103], o.CountBand);
    }

    [Fact]
    public void Parse_Defaults()
    {
        var o = BackfillArgs.Parse([], Today);
        Assert.Equal("sp100", o.Universe);
        Assert.Equal(Today, o.AsOf);          // AsOf defaults to the caller's "today"
        Assert.Equal(20, o.BackfillYears);
        Assert.False(o.DryRun);
    }

    // P1R-10 (finding 149): sp500 is rejected at parse with the real reason (unwired providers), NOT
    // accepted-then-failed at the count-sanity gate ~300 API calls in. Fails on the pre-fix code, which
    // returned band [495,510].
    [Fact]
    public void Parse_Sp500_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--universe", "sp500"], Today));
        Assert.Contains("not wired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownFlag_FailsClosed() => Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--bogus"], Today));

    [Fact]
    public void Parse_MissingValue_FailsClosed() => Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--universe"], Today));

    [Fact]
    public void Parse_NonIntegerYears_FailsClosed() => Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--years", "sp100"], Today));

    // A flag-shaped value must NOT be swallowed — else a requested --dry-run silently becomes a LIVE run.
    [Fact]
    public void Parse_FlagShapedValue_FailsClosed() => Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--as-of", "--dry-run"], Today));

    [Fact]
    public void Parse_NonPositiveYears_FailsClosed()
    {
        Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--years", "-5"], Today));
        Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--years", "0"], Today));
    }

    [Fact]
    public void Parse_UnknownUniverse_FailsClosed() => Assert.Throws<ArgumentException>(() => BackfillArgs.Parse(["--universe", "sp42"], Today));

    // P1R-11: --preflight is parsed like --dry-run (a flag, no value).
    [Fact]
    public void Parse_Preflight()
    {
        Assert.True(BackfillArgs.Parse(["--preflight"], Today).Preflight);
        Assert.False(BackfillArgs.Parse([], Today).Preflight); // default off
    }

    // Config supplies the default years; an explicit --years overrides it (CLI > config).
    [Fact]
    public void Parse_YearsPrecedence()
    {
        Assert.Equal(15, BackfillArgs.Parse([], Today, defaultYears: 15).BackfillYears);        // config default
        Assert.Equal(7, BackfillArgs.Parse(["--years", "7"], Today, defaultYears: 15).BackfillYears); // CLI wins
    }

    [Fact]
    public void DerivedFields()
    {
        var o = BackfillArgs.Parse(["--as-of", "2026-07-13", "--years", "20"], Today);
        Assert.Equal("2006-07-13", o.From);                  // AsOf − BackfillYears
        Assert.Equal("2026-07-13T22:00:00Z", o.ObservedAt);
        Assert.Equal((1996, 2056), o.CalendarYears);         // ±30y
    }
}
