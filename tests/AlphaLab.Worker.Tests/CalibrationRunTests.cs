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
