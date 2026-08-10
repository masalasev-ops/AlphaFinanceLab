using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-Calibration (checkpoint 4.8): the full `replay-calibrate` chain over the CI-mini arena —
/// report archived with every mandatory section, calibrated values frozen as APPEND-ONLY versioned
/// config rows (never UPDATE), `--report-only` writing nothing, and a second freeze appending v2.
/// </summary>
public class CalibrationRunTests
{
    private static IConfiguration Config(string reportDir) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Populations:Size"] = "6",
            ["Populations:CostFreeSize"] = "3",
            ["Calibration:Plant:SeedsPerPlant"] = "2",
            ["Calibration:ReportDir"] = reportDir,
        }).Build();

    private static CalibrationOrchestrator Orchestrator(string reportDir) =>
        new(Config(reportDir), new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, NullLoggerFactory.Instance);

    private static ReplayRequest Window(PipelineHarness h) =>
        new(h.Sessions[5], h.Sessions[35], LearnThrough: h.Sessions[30]);

    /// <summary>
    /// 6.5 — a run that SIMULATES NOTHING is refused, and no report is archived.
    ///
    /// This is the trap D117 clause 2's confirmation slice walks into. The slice exists to validate a
    /// CHANGED rule on re-simulated sessions ("parity exercises the unchanged path, so it structurally
    /// cannot validate the changed one"), but the obvious command against an arena that already holds the
    /// generation skips every session — so it would validate the new rule using outputs the OLD rule
    /// produced, and archive a markdown file that reads exactly like a confirmation. A false negative with
    /// a filename is worse than no report, because it is citable.
    ///
    /// The only flag that forces re-simulation is --reset, which deletes the whole generation. So the
    /// operator's two obvious moves were a fake pass and a destroyed sign-off; this removes the first.
    /// </summary>
    [Fact]
    public async Task FX_Calibration_AZeroSessionRun_IsRefused_AndArchivesNoReport()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal0-" + Guid.NewGuid().ToString("N"));
        try
        {
            // First run simulates the window and archives its report.
            Assert.Equal(0, await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false));
            var afterFirst = Directory.GetFiles(Path.Combine(reportDir, "sp500"), "*-calibration.md").Length;

            // The SAME command again: every session is already committed, so nothing is simulated.
            var exit = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false);

            Assert.Equal(1, exit);
            // ...and it stopped BEFORE archiving: no new report, so nothing exists to be mistaken later
            // for confirmation evidence.
            Assert.Equal(afterFirst, Directory.GetFiles(Path.Combine(reportDir, "sp500"), "*-calibration.md").Length);
        }
        finally
        {
            try { Directory.Delete(reportDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task FX_Calibration_ReportGeneratedAndConfigRowsFrozen()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exit = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false);
            Assert.Equal(0, exit);

            // The archived report, with every mandatory section (D64's sensitivity section is PERMANENT).
            var report = Assert.Single(Directory.GetFiles(Path.Combine(reportDir, "sp500"), "*-calibration.md"));
            var text = File.ReadAllText(report);
            Assert.Contains("D56 trajectory curves", text);
            Assert.Contains("Plant sensitivity — naive vs realistic (PERMANENT section", text);
            Assert.Contains("C-1 detection power", text);
            Assert.Contains("Machinery verification + KPIs", text);
            Assert.Contains("Per-signal false-alarm contribution", text);
            Assert.Contains("Data vintage (D64 stamp)", text);
            Assert.Contains("C-2 sampling band", text);
            // finding 278: the report records which build produced its numbers (the sign-off DoD).
            Assert.Contains("Build configuration:", text);
            Assert.Contains(CalibrationOrchestrator.BuildConfiguration(), text);
            // D107: the reported-only members carry their citation in the outcome cell, and the
            // generation-provenance line records which build computed the verification.
            Assert.Contains("reported-only per D107", text);
            Assert.Contains("Generation provenance (D107)", text);

            using var db = h.Open();
            // The frozen rows: v1 of each D98 key, append-only.
            foreach (var key in new[]
                     {
                         CalibratedKeys.PNoiseCurve("daily"), CalibratedKeys.PEdgeCurve("daily"),
                         CalibratedKeys.DetectionPower, CalibratedKeys.S6AutoRetireEvals, CalibratedKeys.ReportRef,
                     })
            {
                var row = Assert.Single(db.Config.Where(c => c.Key == key).ToList());
                Assert.Equal(1, row.Version);
            }

            // The frozen curves round-trip and interpolate.
            var noise = S3Curve.FromJson(db.Config.Single(c => c.Key == CalibratedKeys.PNoiseCurve("daily")).ValueJson);
            Assert.Equal("p_noise", noise.Kind);
            Assert.NotEmpty(noise.Knots);
            Assert.NotNull(noise.Vintage);

            // The C-1 sweep is the MONTHLY ladder rungs (2/4/8/16 at defaults; Change 4).
            var power = db.Config.Single(c => c.Key == CalibratedKeys.DetectionPower).ValueJson;
            Assert.Contains("\"2\"", power);
            Assert.Contains("\"4\"", power);
            Assert.Contains("\"8\"", power);
            Assert.Contains("\"16\"", power);

            // A SECOND freeze appends v2 — never an UPDATE (finding 108; the CI grep guards the SQL side,
            // this guards the semantics). This is the documented RESUME path: the replay is finished, so
            // every session skips as already-committed and only verification + freeze re-run. It must say
            // so explicitly (6.5) — the same shape refuses by default, because a zero-session run that
            // archives a report is a false confirmation.
            var exit2 = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false,
                allowNoNewSessions: true);
            Assert.Equal(0, exit2);
            Assert.Equal(2, db.Config.Count(c => c.Key == CalibratedKeys.PEdgeCurve("daily")));
        }
        finally
        {
            try { Directory.Delete(reportDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Phase-4 review: a hard verification FAILURE must stop the chain BEFORE any config write — config
    // is append-only, so a frozen-then-failed generation could only be papered over, never removed,
    // and the next forward run would judge S3 against the failed calibration's curves.
    [Fact]
    public async Task FX_Calibration_VerificationFailure_ArchivesReportButFreezesNothing()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Force promotions_le_chance to FAIL: pre-plant replay 'promotions' of every no-edge plant
            // (2 of 2 promoted >> the binomial chance bound of 1 at the CI scale).
            var plant = new CalibrationOptions().Plant;
            plant.SeedsPerPlant = 2;
            var specs = PlantCohorts.Build(plant,
                Evaluation.Populations.PopulationFamilies.ForPhase3(new PopulationsOptions { Size = 6, CostFreeSize = 3 }));
            using (var db = h.Open())
            {
                foreach (var id in specs.Where(s => s.Kind == PlantKind.NoEdge).Select(s => s.StrategyId))
                {
                    db.GoLiveLog.Add(new Data.Entities.GoLiveLogRow
                    {
                        AsOf = h.Sessions[20], Promoted = id, Verdict = "GoLive", EvidenceJson = "{}", RunKind = "replay",
                    });
                }
                db.SaveChanges();
            }

            var exit = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false);
            Assert.Equal(1, exit);

            // The evidence is archived; the store's config is untouched.
            Assert.Single(Directory.GetFiles(Path.Combine(reportDir, "sp500"), "*-calibration.md"));
            using (var db = h.Open())
            {
                foreach (var key in new[]
                         {
                             CalibratedKeys.PNoiseCurve("daily"), CalibratedKeys.PEdgeCurve("daily"),
                             CalibratedKeys.DetectionPower, CalibratedKeys.S6AutoRetireEvals, CalibratedKeys.ReportRef,
                         })
                {
                    Assert.Empty(db.Config.Where(c => c.Key == key).ToList());
                }
            }
        }
        finally
        {
            try { Directory.Delete(reportDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Phase-4 review: the S6 patience knob is seeded from the FIRST freeze only (D98 seed-once). A re-run
    // after an operator raise (a new version of Monitor.S6.AutoRetireEvals) must never re-stamp the
    // Appendix-A default over it.
    //
    // CITATION CORRECTED (D141 sweep): this cited "the RUNBOOK §8.4 operator move" and "the documented
    // recalibration loop". There is no §8.4, and RUNBOOK:148 records that the "raise
    // `Monitor.S6.AutoRetireEvals` and re-run" loop was proven NOT to converge (finding 270; it is gone).
    // The test is still right and the mechanism unchanged — what was wrong was the reason given for it.
    // D98's seed-once is the reason: the value is frozen from the first calibration, and a later version
    // is the operator's, not the chain's to overwrite.
    [Fact]
    public async Task FX_Calibration_Rerun_NeverRestampsOperatorPatience()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exit = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false);
            Assert.Equal(0, exit);

            using (var db = h.Open())
            {
                Assert.Equal("4", db.Config.Single(c => c.Key == CalibratedKeys.S6AutoRetireEvals).ValueJson);

                // The operator move: raise the patience via a NEW version (rule 24, append-only).
                db.Config.Add(new Data.Entities.ConfigRow
                {
                    Key = CalibratedKeys.S6AutoRetireEvals, ValueJson = "6", Version = 2,
                    ChangedOn = "2026-07-22T00:00:00Z", Reason = "operator: survival-floor recalibration (finding 113)",
                });
                db.SaveChanges();
            }

            // The RESUME path again (6.5): the replay is finished, so every session skips and only
            // verification + freeze re-run. Opt-in, because the same shape refuses by default.
            var exit2 = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false,
                allowNoNewSessions: true);
            Assert.Equal(0, exit2);

            using (var db = h.Open())
            {
                // No v3 = 4 clobber: the operator's 6 is still the resolved current value.
                Assert.Equal(2, db.Config.Count(c => c.Key == CalibratedKeys.S6AutoRetireEvals));
                Assert.Equal("6", new AlphaLab.Data.Services.ConfigReadService(db)
                    .ResolveCurrent(CalibratedKeys.S6AutoRetireEvals));
            }
        }
        finally
        {
            try { Directory.Delete(reportDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// **D141 CARVE-OUT 2, ENFORCED — `CalibrationOrchestrator:194` MUST resolve CURRENT, never as-of.**
    ///
    /// D141 converts run-time-current config reads on reproducible paths to `ResolveAsOf`. This site is
    /// classified REPLAY by that decision's own criterion — the archived pair proves it was reproduced with
    /// a differing answer (`docs/calibration/sp500/2026-07-31-calibration.md:10` freezes
    /// `Monitor.S6.AutoRetireEvals`; the 2026-08-03 report, same generation and same frozen watermark, does
    /// not) — and it must STILL stay latest-wins, because as-of resolution cannot express it: the chain
    /// stamps its own config rows with wall-clock `ChangedOn = DateTime.UtcNow`
    /// (`CalibrationOrchestrator.cs:175-176`), which is ALWAYS later than the frozen DATA watermark. An
    /// as-of read would therefore return null on every run, flip `patienceAlreadySet` to false, and
    /// re-stamp the Appendix-A default over the stored value on every pass — breaking D98's seed-once.
    ///
    /// This is a TEST and not a comment on purpose. "Conversion forbidden" written as a comment is a claim
    /// nothing checks, which is the exact defect D140 names; the next sweep reading a REPLAY label would
    /// convert the site and every fixture above would still pass except this one.
    /// </summary>
    [Fact]
    public async Task FX_D141_CarveOut2_PatienceGuardResolvesCurrent_NotAsOf()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A patience row stamped strictly AFTER any watermark this run can resolve — the shape the
            // chain's OWN writes take, since they carry a wall-clock instant while the watermark is the
            // frozen data instant. ResolveCurrent sees it; ResolveAsOf(watermark) cannot.
            using (var db = h.Open())
            {
                db.Config.Add(new Data.Entities.ConfigRow
                {
                    Key = CalibratedKeys.S6AutoRetireEvals, ValueJson = "7", Version = 1,
                    ChangedOn = "2099-01-01T00:00:00Z", Reason = "D141 carve-out 2 fixture: stamped after the watermark",
                });
                db.SaveChanges();
            }

            var exit = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false);
            Assert.Equal(0, exit);

            using (var db = h.Open())
            {
                // The guard SAW the row: no second version was appended over it. Under an as-of read the
                // row is invisible, patienceAlreadySet goes false, and this becomes 2 rows with the
                // default 4 winning — which is precisely the regression this fixture exists to catch.
                Assert.Equal(1, db.Config.Count(c => c.Key == CalibratedKeys.S6AutoRetireEvals));
                Assert.Equal("7", new AlphaLab.Data.Services.ConfigReadService(db)
                    .ResolveCurrent(CalibratedKeys.S6AutoRetireEvals));
            }
        }
        finally
        {
            try { Directory.Delete(reportDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// **D141's pre-flight, ENFORCED: an unpinned confirmation slice is refused.** `--reset --report-only`
    /// against a store that already holds a committed generation is the slice's signature, and without an
    /// explicit `--watermark` it re-simulates that generation at the store's CURRENT high-water mark —
    /// a different vintage from the one it is confirming, with nothing in the report to say so.
    /// </summary>
    [Fact]
    public async Task FX_D141_ConfirmationSlice_RefusesUnpinnedReset_WhenAGenerationExists()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A committed generation to confirm.
            Assert.Equal(0, await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false));

            // The slice, unpinned: refused rather than run at the wrong vintage.
            var unpinned = Window(h) with { Reset = true };
            Assert.Equal(1, await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", unpinned, reportOnly: true));

            // …and PINNED, it is admitted — the guard is a pin requirement, not a prohibition. (It stops at
            // the zero-session refusal or runs on; either way it is past the D141 check, which is what this
            // asserts: the run was not rejected for want of a watermark.)
            var pinned = Window(h) with { Reset = true, Watermark = "2026-01-01T22:00:00Z" };
            await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", pinned, reportOnly: true,
                allowNoNewSessions: true);
        }
        finally
        {
            try { Directory.Delete(reportDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task FX_Calibration_ReportOnly_WritesNoConfigRows()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            // D141: snapshot the WHOLE config table, not three keys. The report renders "Config rows frozen
            // this run: (none — report-only …)", and a per-key assertion verifies a narrower claim than the
            // one the artefact makes — the D140 shape. Any config INSERT anywhere on the path (the roster
            // bootstrap included) has to show up here.
            static List<string> Snapshot(AlphaLab.Data.AlphaLabDbContext db) =>
                [.. db.Config.Select(c => new { c.Key, c.Version }).AsEnumerable()
                    .Select(c => c.Key + "@v" + c.Version).OrderBy(s => s, StringComparer.Ordinal)];

            List<string> before;
            using (var pre = h.Open()) before = Snapshot(pre);

            var exit = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: true);
            Assert.Equal(0, exit);

            Assert.Single(Directory.GetFiles(Path.Combine(reportDir, "sp500"), "*-calibration.md"));
            using var db = h.Open();
            Assert.Empty(db.Config.Where(c => c.Key.StartsWith("Monitor.S3.")).ToList());
            Assert.Empty(db.Config.Where(c => c.Key == CalibratedKeys.ReportRef).ToList());

            // THE PRECISE CLAIM, because the broad one is FALSE and measuring it is how that was found:
            // a --report-only run writes EXACTLY ONE config row on a store that has never opened accounts,
            // `Accounts.StartingCash@v1`, from the roster bootstrap (DummyRoster.ResolveStartingCash). That
            // write is deliberate and stays: finding K records the opening capital so the value the accounts
            // opened at is auditable rather than a literal only the code knew, and suppressing it under a
            // flag the roster cannot see would reintroduce exactly that. It does NOT fire on the D139
            // confirmation slice, whose byte copy already carries the key (live sp500: v1, 2006-01-03).
            //
            // So the assertion is: nothing else, ever. Any other key appearing under --report-only is the
            // regression this guards — and "no config rows" as a blanket claim is retired here rather than
            // left standing as a sentence the run does not honour (D140).
            var permittedBootstrap = AlphaLab.Strategies.DummyRoster.StartingCashConfigKey + "@v1";
            Assert.Equal(
                before.Union([permittedBootstrap]).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                Snapshot(db));
        }
        finally
        {
            try { Directory.Delete(reportDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void WorkerCommand_ReplayCalibrate_ParsesTheFullShape()
    {
        var command = WorkerCommandParser.Parse(
        [
            "replay-calibrate", "--from", "2010-01-04", "--to", "2025-06-30",
            "--learn-through", "2020-12-31", "--watermark", "2026-07-22T14:00:00Z", "--reset", "--report-only",
        ]);
        Assert.Equal(WorkerCommandKind.ReplayCalibrate, command.Kind);
        Assert.True(command.ReportOnly);
        Assert.NotNull(command.Replay);
        Assert.Equal("2010-01-04", command.Replay!.From);
        Assert.Equal("2020-12-31", command.Replay.LearnThrough);
        Assert.Equal("2026-07-22T14:00:00Z", command.Replay.Watermark);
        Assert.True(command.Replay.Reset);

        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(["replay-calibrate", "--from", "2010-01-04"]));
        Assert.Throws<ArgumentException>(() => WorkerCommandParser.Parse(
            ["replay-calibrate", "--from", "2025-06-30", "--to", "2010-01-04"]));
    }

    // finding 276: the archived report is the Phase-4 sign-off artifact and MUST land in the TRACKED
    // docs/calibration — but .NET 10 `dotnet run --project src/AlphaLab.Worker` runs with cwd = the
    // project dir, so a bare relative "docs/calibration" wrote under src/AlphaLab.Worker/ on the smoke
    // run. A relative ReportDir is now anchored to the git repo root; an absolute one is honored verbatim.

    [Fact]
    public void FX_ReportPath_AbsoluteDir_HonoredVerbatim()
    {
        // What the tests inject (an absolute temp dir) must pass through untouched — repoRoot ignored.
        var abs = Path.Combine(Path.GetTempPath(), "alphalab-report");
        Assert.True(Path.IsPathRooted(abs));
        Assert.Equal(abs, CalibrationOrchestrator.ResolveReportBaseDir(abs, "C:/some/repo/root"));
    }

    [Fact]
    public void FX_ReportPath_RelativeDir_AnchoredToGivenRepoRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "alphalab-repo-root");
        Assert.Equal(Path.Combine(root, "docs/calibration"),
            CalibrationOrchestrator.ResolveReportBaseDir("docs/calibration", root));
    }

    [Fact]
    public void FX_ReportPath_RelativeDir_NullRoot_FallsBackToCurrentDirectory()
    {
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "docs/calibration"),
            CalibrationOrchestrator.ResolveReportBaseDir("docs/calibration", repoRoot: null));
    }

    [Fact]
    public void FX_ReportPath_FindRepoRoot_ResolvesToTrackedDocsCalibration()
    {
        // The test bin sits inside the repo, so discovery walks up to the real root — proving that on a
        // real launch the default "docs/calibration" resolves to the git-tracked dir (verify-it-lands-there).
        var root = CalibrationOrchestrator.FindRepoRoot();
        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(root!, "src", "AlphaLab.Worker")), "repo root must contain src/AlphaLab.Worker");
        var reportBase = CalibrationOrchestrator.ResolveReportBaseDir("docs/calibration", root);
        Assert.True(Directory.Exists(reportBase), $"the resolved report base must be the tracked docs/calibration: {reportBase}");
    }
}
