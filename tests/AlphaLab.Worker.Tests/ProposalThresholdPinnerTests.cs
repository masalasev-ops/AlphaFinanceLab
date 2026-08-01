using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Candidates;
using AlphaLab.Worker.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// `pin-proposal-thresholds` (checkpoint 5.7, D110) — the ONE sanctioned way to satisfy the hypotheses
/// endpoint's pin refusal, since rule 15 forbids editing the store by hand.
/// </summary>
public class ProposalThresholdPinnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alphalab-pin-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public ProposalThresholdPinnerTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "alphalab.db");
        using var db = Open();
        db.Database.Migrate();
    }

    private AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private ProposalThresholdPinner Pinner() => new(
        new ConfigurationBuilder().Build(),
        new ArenaOptions { Id = "sp500" },
        NullLoggerFactory.Instance);

    [Fact]
    public void Pinning_WritesBothKeysAtVersion1_WithTheirDerivationInTheReason()
    {
        var outcome = Pinner().Run($"Data Source={_dbPath}", new ProposalPinRequest(0.02, 10));

        Assert.Equal(2, outcome.Written.Count);
        Assert.Empty(outcome.AlreadyPinned);

        using var db = Open();
        var rows = db.Config.Where(c => ProposalScoreKeys.All.Contains(c.Key)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.Version));

        // The DERIVATION lives in the row, not in a commit message: this is the mechanism that makes an
        // operator-chosen value auditable later, and it is the whole reason the verb exists rather than a
        // hand-edit.
        var clamp = rows.Single(r => r.Key == ProposalScoreKeys.PriorClamp);
        Assert.Equal("0.02", clamp.ValueJson);
        Assert.Contains("unbounded at 0 and 1", clamp.Reason!, StringComparison.Ordinal);

        var minClosed = rows.Single(r => r.Key == ProposalScoreKeys.ScoreMinClosed);
        Assert.Equal("10", minClosed.ValueJson);
        Assert.Contains("noise about noise", minClosed.Reason!, StringComparison.Ordinal);

        // And the endpoint's precondition is now satisfied — the two sides agree on the key names because
        // they read the same constants.
        Assert.Empty(ProposalScoreKeys.Unpinned(db));
    }

    [Fact]
    public void AnAlreadyPinnedKey_IsLeftUntouched_AndReported()
    {
        var pinner = Pinner();
        pinner.Run($"Data Source={_dbPath}", new ProposalPinRequest(0.02, 10));

        // The whole point of pinning before the first proposal is that the parameter cannot be revised
        // once scores exist to look at (D110 R3: change the researcher's INPUTS, never the measurement).
        var second = pinner.Run($"Data Source={_dbPath}", new ProposalPinRequest(0.40, 999));

        Assert.Empty(second.Written);
        Assert.Equal(2, second.AlreadyPinned.Count);

        using var db = Open();
        Assert.Equal("0.02", db.Config.Single(c => c.Key == ProposalScoreKeys.PriorClamp).ValueJson);
        Assert.Equal(1, db.Config.Count(c => c.Key == ProposalScoreKeys.PriorClamp));   // no version 2
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(-0.1)]
    public void AnOutOfRangeClamp_IsRefused_NotClamped(double clamp)
    {
        // At 0.5 every prior collapses to the same value and the calibration channel measures nothing —
        // a silent failure, which is why the verb refuses rather than clamping the clamp.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Pinner().Run($"Data Source={_dbPath}", new ProposalPinRequest(clamp, 10)));

        using var db = Open();
        Assert.Empty(db.Config.Where(c => ProposalScoreKeys.All.Contains(c.Key)).ToList());
    }

    [Fact]
    public void AZeroMinClosed_IsRefused()
    {
        // A base rate estimated from zero closed outcomes is not an estimate.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Pinner().Run($"Data Source={_dbPath}", new ProposalPinRequest(0.02, 0)));
    }

    [Fact]
    public void TheVerbRequiresBothValuesExplicitly()
    {
        // Unlike signal-pin-thresholds' optional --power, BOTH of these govern a published SCORE rather
        // than a diagnostic, so a missing value silently defaulting would record a decision nobody made.
        Assert.Throws<ArgumentException>(() =>
            WorkerCommandParser.Parse(["pin-proposal-thresholds", "--prior-clamp", "0.02"]));
        Assert.Throws<ArgumentException>(() =>
            WorkerCommandParser.Parse(["pin-proposal-thresholds", "--min-closed", "10"]));
        Assert.Throws<ArgumentException>(() =>
            WorkerCommandParser.Parse(["pin-proposal-thresholds", "--prior-clamp", "0.9", "--min-closed", "10"]));

        var cmd = WorkerCommandParser.Parse(["pin-proposal-thresholds", "--prior-clamp", "0.02", "--min-closed", "10"]);
        Assert.Equal(WorkerCommandKind.PinProposalThresholds, cmd.Kind);
        Assert.Equal(0.02, cmd.ProposalPin!.PriorClamp);
        Assert.Equal(10, cmd.ProposalPin.MinClosed);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
