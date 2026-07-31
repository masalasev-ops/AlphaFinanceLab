using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Worker;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-SignalPinBeforeGrade (D108 / FR-45): the anti-curve-fitting ordering is enforced by a SHAPE, not
/// by a comment. The backfill refuses to grade while either significance level is unpinned, and the
/// refusal is paired with its negative half — a guard that only ever passes is a tautology
/// (the `ConfigConsistencyTests` discipline).
/// </summary>
public class SignalBackfillTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Arena:Id"] = "sp500",
            ["SignalLibrary:HorizonsDays:0"] = "5",
        }).Build();

    private static SignalBackfillRunner Runner() =>
        new(Config(), new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, NullLoggerFactory.Instance);

    private static void Pin(AlphaLabDbContext db, string key, string value) =>
        db.Config.Add(new ConfigRow
        {
            Key = key, ValueJson = value, Version = 1,
            ChangedOn = "2026-01-01T00:00:00Z", Reason = "checkpoint 4.5.2 pin (D108)",
        });

    [Fact]
    public async Task FX_SignalPinBeforeGrade_RefusesWhileEitherThresholdIsUnpinned_AndWritesNothing()
    {
        using var h = new PipelineHarness();
        var cs = $"Data Source={h.DbPath}";

        // (a) NEITHER pinned — refuse, naming both keys.
        var both = await Assert.ThrowsAsync<SignalThresholdsNotPinnedException>(
            () => Runner().RunAsync(cs, new SignalBackfillRequest(h.Sessions[0], h.Sessions[^1])));
        Assert.Contains(SignalBackfillRunner.GoneAlphaKey, both.MissingKeys);
        Assert.Contains(SignalBackfillRunner.DecayAlphaKey, both.MissingKeys);

        using (var db = h.Open())
        {
            Assert.Empty(db.SignalIc.ToList());   // and NOTHING was written
        }

        // (b) ONE pinned is still a refusal — the guard is on the pair, not on "any".
        using (var db = h.Open())
        {
            Pin(db, SignalBackfillRunner.GoneAlphaKey, "0.05");
            db.SaveChanges();
        }

        var one = await Assert.ThrowsAsync<SignalThresholdsNotPinnedException>(
            () => Runner().RunAsync(cs, new SignalBackfillRequest(h.Sessions[0], h.Sessions[^1])));
        Assert.Equal([SignalBackfillRunner.DecayAlphaKey], one.MissingKeys);   // only the missing one is named

        using (var db = h.Open())
        {
            Assert.Empty(db.SignalIc.ToList());
        }
    }

    [Fact]
    public async Task FX_SignalPinBeforeGrade_NegativeHalf_OncePinnedItRuns()
    {
        // The half that stops the guard being a tautology: with BOTH thresholds pinned, the same call
        // that refused above proceeds and grades. Without this, a guard that refused unconditionally
        // would pass its own test.
        using var h = new PipelineHarness();
        using (var db = h.Open())
        {
            Pin(db, SignalBackfillRunner.GoneAlphaKey, "0.05");
            Pin(db, SignalBackfillRunner.DecayAlphaKey, "0.05");
            db.SaveChanges();
        }

        var outcome = await Runner().RunAsync(
            $"Data Source={h.DbPath}", new SignalBackfillRequest(h.Sessions[0], h.Sessions[^1]));

        Assert.True(outcome.SessionsPlanned > 0);
        using var check = h.Open();
        Assert.Equal(7, check.Signals.Count());   // the frozen registry landed
    }

    [Fact]
    public async Task Backfill_IsResumableAndIdempotent_ARerunWritesNothingNew()
    {
        // The rows themselves are the progress marker (no cursor table), so a completed run re-run is a
        // no-op and an interrupted one resumes by not re-grading what it already wrote.
        using var h = new PipelineHarness();
        using (var db = h.Open())
        {
            Pin(db, SignalBackfillRunner.GoneAlphaKey, "0.05");
            Pin(db, SignalBackfillRunner.DecayAlphaKey, "0.05");
            db.SaveChanges();
        }
        var cs = $"Data Source={h.DbPath}";
        var request = new SignalBackfillRequest(h.Sessions[0], h.Sessions[^1]);

        var first = await Runner().RunAsync(cs, request);
        var second = await Runner().RunAsync(cs, request);

        Assert.Equal(0, second.GradesWritten);
        Assert.Equal(first.GradesWritten, second.GradesAlreadyPresent);

        using var db2 = h.Open();
        var rows = db2.SignalIc.ToList();
        Assert.Equal(first.GradesWritten, rows.Count);
        // No run row, no generation: this is NOT a replay (D95), and signal_ic carries no run_kind.
        Assert.Empty(db2.Runs.Where(r => r.RunKind == "replay").ToList());
    }

    [Fact]
    public void TheVerbParses_AndRejectsAnInvertedWindow()
    {
        var cmd = WorkerCommandParser.Parse(
            ["signal-backfill", "--from", "2006-01-01", "--to", "2026-01-01", "--arena", "sp500"]);
        Assert.Equal(WorkerCommandKind.SignalBackfill, cmd.Kind);
        Assert.Equal("2006-01-01", cmd.SignalBackfill!.From);
        Assert.Equal("2026-01-01", cmd.SignalBackfill.To);
        Assert.Equal("sp500", cmd.ArenaId);

        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(
            ["signal-backfill", "--from", "2026-01-01", "--to", "2006-01-01"]));
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(["signal-backfill", "--from", "2006-01-01"]));
    }
}
