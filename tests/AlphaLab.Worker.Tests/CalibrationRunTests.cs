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

            // The C-1 sweep covers the three alpha levels (2/4/8 at defaults).
            var power = db.Config.Single(c => c.Key == CalibratedKeys.DetectionPower).ValueJson;
            Assert.Contains("\"2\"", power);
            Assert.Contains("\"4\"", power);
            Assert.Contains("\"8\"", power);

            // A SECOND freeze appends v2 — never an UPDATE (finding 108; the CI grep guards the SQL side,
            // this guards the semantics).
            var exit2 = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false);
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

    // Phase-4 review: the S6 patience knob is seeded from the FIRST freeze only. A re-run after the
    // operator's finding-113 raise (a new version of Monitor.S6.AutoRetireEvals) must never re-stamp
    // the Appendix-A default over it — else the documented recalibration loop can never converge.
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

                // The RUNBOOK §8.4 operator move: raise the patience via a NEW version (rule 24).
                db.Config.Add(new Data.Entities.ConfigRow
                {
                    Key = CalibratedKeys.S6AutoRetireEvals, ValueJson = "6", Version = 2,
                    ChangedOn = "2026-07-22T00:00:00Z", Reason = "operator: survival-floor recalibration (finding 113)",
                });
                db.SaveChanges();
            }

            var exit2 = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: false);
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

    [Fact]
    public async Task FX_Calibration_ReportOnly_WritesNoConfigRows()
    {
        using var h = new PipelineHarness();
        var reportDir = Path.Combine(Path.GetTempPath(), "alphalab-cal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var exit = await Orchestrator(reportDir).RunAsync($"Data Source={h.DbPath}", Window(h), reportOnly: true);
            Assert.Equal(0, exit);

            Assert.Single(Directory.GetFiles(Path.Combine(reportDir, "sp500"), "*-calibration.md"));
            using var db = h.Open();
            Assert.Empty(db.Config.Where(c => c.Key.StartsWith("Monitor.S3.")).ToList());
            Assert.Empty(db.Config.Where(c => c.Key == CalibratedKeys.ReportRef).ToList());
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
}
