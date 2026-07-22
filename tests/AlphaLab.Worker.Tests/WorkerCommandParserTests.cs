namespace AlphaLab.Worker.Tests;

/// <summary>
/// The ops-verb command line (FR-25, v1.9.37). Pure parsing, so the interesting cases need no host —
/// the WorkerModeParserTests precedent.
/// </summary>
public class WorkerCommandParserTests
{
    [Fact]
    public void NoArgs_IsTheDailyLaunch() =>
        Assert.Equal(WorkerCommandKind.Daily, WorkerCommandParser.Parse([]).Kind);

    [Fact]
    public void ServeFlagAlone_IsStillTheDailyLaunch() =>
        // --serve selects Scheduled mode (D61), which WorkerModeParser owns; it is not an ops verb.
        Assert.Equal(WorkerCommandKind.Daily, WorkerCommandParser.Parse(["--serve"]).Kind);

    [Fact]
    public void ReproduceDay_ParsesDateAndArena()
    {
        var command = WorkerCommandParser.Parse(["reproduce-day", "--date", "2026-07-22", "--arena", "sp100"]);

        Assert.Equal(WorkerCommandKind.ReproduceDay, command.Kind);
        Assert.Equal("2026-07-22", command.Date);
        Assert.Equal("sp100", command.ArenaId);
    }

    [Fact]
    public void ReproduceDay_DefaultsArenaToConfig() =>
        Assert.Null(WorkerCommandParser.Parse(["reproduce-day", "--date", "2026-07-22"]).ArenaId);

    [Fact]
    public void ReproduceDay_WithoutADate_FailsClosed() =>
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(["reproduce-day"]));

    [Theory]
    [InlineData("22-07-2026")]
    [InlineData("2026/07/22")]
    [InlineData("yesterday")]
    public void ReproduceDay_WithAMalformedDate_FailsClosed(string date) =>
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(["reproduce-day", "--date", date]));

    [Fact]
    public void VerifyWal_Parses() =>
        Assert.Equal(WorkerCommandKind.VerifyWal, WorkerCommandParser.Parse(["verify-wal"]).Kind);

    [Fact]
    public void UnknownVerb_FailsClosed_RatherThanStartingTheDailyRun()
    {
        // The one that matters: a typo must NOT fall through and launch the sole DB writer against the
        // live arena (rule 10).
        var ex = Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(["reproduce-dya", "--date", "2026-07-22"]));
        Assert.Contains("Unknown command", ex.Message, StringComparison.Ordinal);
    }
}
