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
    public void StoreSweep_Parses_WithAnArena()
    {
        // The D120 stored-corpus sweep: report-only, so the verb takes nothing but the arena.
        var c = WorkerCommandParser.Parse(["store-sweep", "--arena", "sp500"]);
        Assert.Equal(WorkerCommandKind.StoreSweep, c.Kind);
        Assert.Equal("sp500", c.ArenaId);
    }

    [Fact]
    public void UnknownVerb_FailsClosed_RatherThanStartingTheDailyRun()
    {
        // The one that matters: a typo must NOT fall through and launch the sole DB writer against the
        // live arena (rule 10).
        var ex = Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(["reproduce-dya", "--date", "2026-07-22"]));
        Assert.Contains("Unknown command", ex.Message, StringComparison.Ordinal);
    }

    // ---- replay-recompute (D106/D117) -----------------------------------------------------------------

    /// <summary>A BARE `replay-recompute` is the §25.3 parity run: no overrides means recompute generation
    /// 1 under its own rules and compare against its own records. Making that the default matters — the
    /// parity check is the one run that must happen before any other is trusted, so it should not need a
    /// flag to remember.</summary>
    [Fact]
    public void ReplayRecompute_WithNoOverrides_IsTheParityRun()
    {
        var c = WorkerCommandParser.Parse(["replay-recompute", "--arena", "sp500"]);
        Assert.Equal(WorkerCommandKind.ReplayRecompute, c.Kind);
        Assert.Equal("sp500", c.ArenaId);
        Assert.Empty(c.Recompute!.Overrides);
        Assert.True(c.Recompute.VerifyParity);
    }

    [Fact]
    public void ReplayRecompute_ParsesRepeatedSetPairs()
    {
        var c = WorkerCommandParser.Parse(
            ["replay-recompute", "--set", "monitor.s6.sustain_evals=4", "--set", "gate.alpha_definition=jensen", "--name", "gen2"]);
        Assert.Equal("gen2", c.Recompute!.SpecName);
        Assert.Equal(2, c.Recompute.Overrides.Count);
        Assert.Equal("4", c.Recompute.Overrides["monitor.s6.sustain_evals"]);
        Assert.Equal("jensen", c.Recompute.Overrides["gate.alpha_definition"]);
        Assert.False(c.Recompute.VerifyParity);   // a spec with overrides is not a parity run
    }

    [Fact]
    public void ReplayRecompute_MalformedSet_Fails_RatherThanBeingIgnored()
    {
        // A silently dropped override would score the WRONG rule change and report it as the right one.
        Assert.Throws<ArgumentException>(() =>
            WorkerCommandParser.Parse(["replay-recompute", "--set", "monitor.s6.sustain_evals"]));
        Assert.Throws<ArgumentException>(() =>
            WorkerCommandParser.Parse(["replay-recompute", "--set", "=4"]));
    }
}
