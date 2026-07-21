using AlphaLab.Core.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// The 21-day evaluation step wired into the pipeline post-commit (checkpoint 3.4). The shared harness has
/// only three run days, so these lower the cadence to 3 to reach an evaluation day; the evaluation math is
/// pinned by the AlphaLab.Evaluation synthetic-arena tests.
/// </summary>
public class EvaluationPipelineTests
{
    [Fact]
    public async Task NonEvaluationDay_WritesNoPowerReports()
    {
        using var h = new PipelineHarness();   // default cadence 21 — three run days never reach it
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        await h.RunAsync(h.Run3);

        using var db = h.Open();
        Assert.Empty(db.PowerReports.ToList());
    }

    [Fact]
    public async Task EvaluationDay_ScoresPromotableStrategiesAgainstTheCapWeightBenchmark()
    {
        using var h = new PipelineHarness(
            configure: s => s.AddSingleton(new GateOptions { EvaluationCadenceDays = 3 }));

        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        await h.RunAsync(h.Run3);   // session 3 = an evaluation day at cadence 3

        using var db = h.Open();
        var reports = db.PowerReports.ToList();

        Assert.NotEmpty(reports);
        // The benchmark is the cap-weight Buy&Hold account; the baselines (cw, ew) are never the candidate.
        Assert.All(reports, r => Assert.Equal("buyhold:cw", r.StrategyB));
        Assert.DoesNotContain(reports, r => r.StrategyA is "buyhold:cw" or "buyhold:ew");
        // A ~3-day track is far inside any MDE ⇒ TooEarly, and every report is a forward ('live') row.
        Assert.All(reports, r => Assert.Equal("TooEarly", r.Verdict));
        Assert.All(reports, r => Assert.Equal("live", r.RunKind));

        // The monitor + allocator run in the same step: overfitting rows + one allocation_log row exist.
        Assert.NotEmpty(db.OverfittingStatus.ToList());
        Assert.Single(db.AllocationLog.ToList());
        Assert.All(db.AllocationLog.ToList(), a => Assert.Equal("live", a.RunKind));

        // Turnover-match (finding 115): a status-neutral turnover_match row is persisted for the candidate.
        var turnover = db.OverfittingChecks.Where(c => c.Signal == "turnover_match").ToList();
        Assert.NotEmpty(turnover);
        Assert.All(turnover, c => Assert.Contains(c.Contribution, new[] { "matched", "caveat" }));
    }
}
