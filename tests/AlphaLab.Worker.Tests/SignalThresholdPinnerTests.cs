using AlphaLab.Core.Config;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// Checkpoint 4.5.2 (D108): the one sanctioned way to satisfy the FR-45 pin refusal. Together with
/// <see cref="SignalBackfillTests"/> this closes the loop — the guard refuses, this verb satisfies it,
/// and the guard then passes. A guard with no legitimate satisfier is a wall, not a gate.
/// </summary>
public class SignalThresholdPinnerTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Arena:Id"] = "sp500",
            ["SignalLibrary:HorizonsDays:0"] = "5",
        }).Build();

    private static SignalThresholdPinner Pinner() =>
        new(Config(), new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, NullLoggerFactory.Instance);

    private static SignalBackfillRunner Backfill() =>
        new(Config(), new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, NullLoggerFactory.Instance);

    [Fact]
    public void Pin_WritesBothRowsAppendOnly_WithTheDerivationRecorded()
    {
        using var h = new PipelineHarness();
        var outcome = Pinner().Run($"Data Source={h.DbPath}", new SignalPinRequest(0.05, 0.05));

        Assert.Equal(2, outcome.Written.Count);
        Assert.Empty(outcome.AlreadyPinned);

        using var db = h.Open();
        foreach (var key in new[] { SignalBackfillRunner.GoneAlphaKey, SignalBackfillRunner.DecayAlphaKey })
        {
            var row = Assert.Single(db.Config.Where(c => c.Key == key).ToList());
            Assert.Equal(1, row.Version);
            Assert.Equal("0.05", row.ValueJson);
            // The DERIVATION travels with the value, so a later reader checks the reasoning rather than
            // trusting a bare number — and can see that the critical value is NOT stored.
            Assert.Contains("D108", row.Reason, StringComparison.Ordinal);
            Assert.Contains("Gate.Confidence", row.Reason, StringComparison.Ordinal);
            Assert.Contains("df", row.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PinnedOnce_NeverReStamped_EvenWithADifferentValue()
    {
        // The whole point of pinning before the first grade row is that the threshold cannot be revised
        // once there are grades to look at. A re-run with a DIFFERENT value must change nothing.
        using var h = new PipelineHarness();
        var cs = $"Data Source={h.DbPath}";
        Pinner().Run(cs, new SignalPinRequest(0.05, 0.05));

        var second = Pinner().Run(cs, new SignalPinRequest(0.20, 0.20));

        Assert.Empty(second.Written);
        Assert.Equal(2, second.AlreadyPinned.Count);

        using var db = h.Open();
        Assert.All(db.Config.Where(c => c.Key.StartsWith("SignalLibrary.")).ToList(), r =>
        {
            Assert.Equal("0.05", r.ValueJson);   // the ORIGINAL value survives
            Assert.Equal(1, r.Version);          // and no version 2 was appended
        });
    }

    [Fact]
    public void ANonSignificanceLevel_IsRefused_NotClamped()
    {
        // A clamped threshold would be a number nobody chose, silently governing a published verdict.
        using var h = new PipelineHarness();
        var cs = $"Data Source={h.DbPath}";

        Assert.Throws<ArgumentOutOfRangeException>(() => Pinner().Run(cs, new SignalPinRequest(0.0, 0.05)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pinner().Run(cs, new SignalPinRequest(0.05, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pinner().Run(cs, new SignalPinRequest(-0.1, 0.05)));

        using var db = h.Open();
        Assert.Empty(db.Config.Where(c => c.Key.StartsWith("SignalLibrary.")).ToList());
    }

    [Fact]
    public async Task TheLoopCloses_RefusedBeforePinning_RunsAfter()
    {
        // The end-to-end contract: the backfill refuses, this verb satisfies it, the backfill proceeds.
        using var h = new PipelineHarness();
        var cs = $"Data Source={h.DbPath}";
        var window = new SignalBackfillRequest(h.Sessions[0], h.Sessions[^1]);

        await Assert.ThrowsAsync<SignalThresholdsNotPinnedException>(() => Backfill().RunAsync(cs, window));

        Pinner().Run(cs, new SignalPinRequest(0.05, 0.05));

        var outcome = await Backfill().RunAsync(cs, window);   // no longer refuses
        Assert.True(outcome.SessionsPlanned > 0);
    }

    [Fact]
    public void ThePowerRow_IsOptional_PinnedOnce_AndCarriesTheDerivationOfTheFloorItScales()
    {
        // finding 305. Power governs the published minimum-detectable-IC, which is a DIAGNOSTIC and
        // never a verdict input - so unlike the two alphas it is optional, and omitting it must leave
        // the store in a state the backfill still accepts.
        using var h = new PipelineHarness();
        var cs = $"Data Source={h.DbPath}";

        var withoutPower = Pinner().Run(cs, new SignalPinRequest(0.05, 0.05));
        Assert.Equal(2, withoutPower.Written.Count);
        using (var db = h.Open())
        {
            Assert.Empty(db.Config.Where(c => c.Key == SignalThresholdPinner.PowerKey).ToList());
        }

        // Pinning it later writes ONLY the new key - the two alphas are reported as already-pinned and
        // are not re-stamped, which is what makes this safe to run after the alphas are already live.
        var second = Pinner().Run(cs, new SignalPinRequest(0.05, 0.05, Power: 0.80));
        Assert.Equal([SignalThresholdPinner.PowerKey], second.Written);
        Assert.Equal(2, second.AlreadyPinned.Count);

        using var check = h.Open();
        var row = Assert.Single(check.Config.Where(c => c.Key == SignalThresholdPinner.PowerKey).ToList());
        Assert.Equal(1, row.Version);
        Assert.Equal("0.8", row.ValueJson);
        // The derivation travels with the value, and it must say the thing that is easy to get wrong:
        // that the POWER TERM is what makes the number a detectable effect rather than a critical value.
        Assert.Contains("Gate.Power", row.Reason, StringComparison.Ordinal);
        Assert.Contains("t_{power", row.Reason, StringComparison.Ordinal);
        Assert.Contains("finding 305", row.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANonProbabilityPower_IsRefused_NotClamped()
    {
        using var h = new PipelineHarness();
        var cs = $"Data Source={h.DbPath}";
        Assert.Throws<ArgumentOutOfRangeException>(() => Pinner().Run(cs, new SignalPinRequest(0.05, 0.05, 1.0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pinner().Run(cs, new SignalPinRequest(0.05, 0.05, 0.0)));

        using var db = h.Open();
        Assert.Empty(db.Config.Where(c => c.Key.StartsWith("SignalLibrary.")).ToList());
    }

    [Fact]
    public void TheVerbParses_AndRequiresBothAlphasExplicitly()
    {
        var cmd = WorkerCommandParser.Parse(
            ["signal-pin-thresholds", "--gone-alpha", "0.05", "--decay-alpha", "0.05", "--arena", "sp500"]);
        Assert.Equal(WorkerCommandKind.SignalPinThresholds, cmd.Kind);
        Assert.Equal(0.05, cmd.SignalPin!.GoneAlpha, 10);
        Assert.Equal(0.05, cmd.SignalPin.DecayAlpha, 10);

        // A MISSING alpha must not default: silently defaulting to 0.05 would defeat the whole
        // pin-before-grade discipline, which requires the operator to state the value deliberately.
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(
            ["signal-pin-thresholds", "--gone-alpha", "0.05"]));
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(
            ["signal-pin-thresholds", "--gone-alpha", "0", "--decay-alpha", "0.05"]));
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(
            ["signal-pin-thresholds", "--gone-alpha", "x", "--decay-alpha", "0.05"]));

        // --power is OPTIONAL (finding 305: it scales a diagnostic, not a verdict)...
        Assert.Null(WorkerCommandParser.Parse(
            ["signal-pin-thresholds", "--gone-alpha", "0.05", "--decay-alpha", "0.05"]).SignalPin!.Power);
        Assert.Equal(0.80, WorkerCommandParser.Parse(
            ["signal-pin-thresholds", "--gone-alpha", "0.05", "--decay-alpha", "0.05", "--power", "0.80"])
            .SignalPin!.Power!.Value, 10);
        // ...but a PRESENT-and-unparseable value is still refused, so a typo cannot become "omitted".
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(
            ["signal-pin-thresholds", "--gone-alpha", "0.05", "--decay-alpha", "0.05", "--power", "eighty"]));
    }
}
