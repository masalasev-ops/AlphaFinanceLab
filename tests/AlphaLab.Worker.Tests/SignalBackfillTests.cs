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

    /// <summary>The horizons the RUNNER will actually use — resolved from the same configuration through
    /// the same accessor, so the fixture cannot drift from the code under test if the binding behaves
    /// unexpectedly.</summary>
    private static IReadOnlyList<int> Horizons() =>
        (Config().GetSection(SignalLibraryOptions.SectionName).Get<SignalLibraryOptions>()
         ?? new SignalLibraryOptions()).ResolvedHorizonsDays;

    /// <summary>Pre-store a COMPLETE (signal × horizon) set for one session, so the coverage check can
    /// be exercised without needing enough history for all seven scorers to fire.</summary>
    private static void PreGrade(AlphaLabDbContext db, string asOf, int signalCount)
    {
        foreach (var s in AlphaLab.Core.Signals.SignalRegistry.V1.Take(signalCount))
        {
            foreach (var k in Horizons())
            {
                db.SignalIc.Add(new SignalIcRow
                { SignalId = s.SignalId, AsOf = asOf, HorizonDays = k, RankIc = 0.01, N = 50 });
            }
        }
    }

    [Fact]
    public void HorizonsDays_BindsFromConfiguration_RatherThanSilentlyKeepingTheDefault()
    {
        // CONFIG_REFERENCE documents SignalLibrary:HorizonsDays as a knob, and the options property is
        // an IReadOnlyList<int> — a shape the configuration binder does not always populate. If it
        // silently kept the class default, an operator editing the key would change nothing and never
        // be told (the finding-286 fail-OPEN shape). Asserted rather than assumed.
        //
        // FINDING 301: written as [5] rather than "contains 5" on purpose. The binder ADDS to a
        // pre-populated collection instead of replacing it, so a property initialised to [21, 63] and
        // configured to [5] bound to [21, 63, 5] — a horizon the operator tried to REMOVE survived, and
        // configuring [21] would have DUPLICATED it. An exact-equality assertion is what caught that;
        // a containment assertion would have passed on the broken shape.
        Assert.Equal([5], Horizons());
    }

    [Fact]
    public void NoConfiguredHorizons_FallsBackToTheDocumentedDefault()
    {
        // The other half of finding 301's fix: the properties are empty by default, so the default has
        // to come from somewhere. If the fallback were dropped the live run would grade NOTHING (the
        // appsettings carries no SignalLibrary section) and the empty-check would read as "fail closed"
        // rather than as the misconfiguration it would actually be.
        var unconfigured = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Arena:Id"] = "sp500" }).Build();
        var options = unconfigured.GetSection(SignalLibraryOptions.SectionName).Get<SignalLibraryOptions>()
                      ?? new SignalLibraryOptions();

        Assert.Empty(options.HorizonsDays);                               // nothing was configured…
        Assert.Equal([21, 63], options.ResolvedHorizonsDays);             // …so the documented default governs
        Assert.Equal([1, 5], options.ResolvedRollingWindowsYears);
    }

    [Fact]
    public async Task AFullyGradedDay_IsSkippedWITHOUTScoringIt()
    {
        // finding 300: the old loop graded every day and discarded the rows at persist time — the right
        // table at the wrong cost, which made "resumable" true only on paper. The observable that proves
        // the fix is SessionsSkippedComplete: that counter increments on a path that `continue`s BEFORE
        // GradeDay is called, so a nonzero value is proof the scoring work did not happen.
        using var h = new PipelineHarness();
        var day = h.Sessions[10];
        using (var db = h.Open())
        {
            Pin(db, SignalBackfillRunner.GoneAlphaKey, "0.05");
            Pin(db, SignalBackfillRunner.DecayAlphaKey, "0.05");
            PreGrade(db, day, signalCount: 7);   // the full set for this day
            db.SaveChanges();
        }

        var outcome = await Runner().RunAsync(
            $"Data Source={h.DbPath}", new SignalBackfillRequest(h.Sessions[0], h.Sessions[^1]));

        Assert.True(outcome.SessionsSkippedComplete >= 1,
            "a day whose full signal x horizon set is already stored must be skipped without scoring");
    }

    [Fact]
    public async Task APartiallyGradedDay_IsREVISITED_SoMissingPairsCanLand()
    {
        // The subtle half: the check is for the FULL set, not "any row". A day left half-graded by an
        // interrupted run — or graded before a signal was registered — must be revisited, or the gap
        // would be frozen in place and no re-run would ever fill it. Skipping on "any row present"
        // would be faster and wrong.
        using var h = new PipelineHarness();
        var day = h.Sessions[10];
        using (var db = h.Open())
        {
            Pin(db, SignalBackfillRunner.GoneAlphaKey, "0.05");
            Pin(db, SignalBackfillRunner.DecayAlphaKey, "0.05");
            PreGrade(db, day, signalCount: 3);   // PARTIAL — 3 of 7
            db.SaveChanges();
        }

        var outcome = await Runner().RunAsync(
            $"Data Source={h.DbPath}", new SignalBackfillRequest(h.Sessions[0], h.Sessions[^1]));

        Assert.Equal(0, outcome.SessionsSkippedComplete);   // nothing was complete, so nothing was skipped
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
