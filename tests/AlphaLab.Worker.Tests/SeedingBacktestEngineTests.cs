using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Replay;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-BacktestEngine (checkpoint 4.10): the walk-forward seeding mode is Arena Replay's RESTRICTED
/// special case — a quarantined equity track + descriptive metrics, with the judging half amputated:
/// it NEVER promotes, monitors, or allocates, and it fails closed on an unknown strategy.
/// </summary>
public class SeedingBacktestEngineTests
{
    private static SeedingBacktestEngine Engine(PipelineHarness h) =>
        new(ReplayEngineTests.HarnessConfiguration(),
            new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" },
            $"Data Source={h.DbPath}", NullLoggerFactory.Instance);

    [Fact]
    public async Task FX_BacktestEngine_NeverPromotes_QuarantinedTrack()
    {
        using var h = new PipelineHarness();
        // A window long enough that the evaluation cadence WOULD have fired (21+ sessions) — proving
        // the toggle amputated it, not that the window was too short to trigger it.
        var result = await Engine(h).RunAsync(new BacktestRequest("buyhold:ew", h.Sessions[5], h.Sessions[35]));

        Assert.Equal("buyhold:ew", result.StrategyId);
        Assert.Equal(31, result.Equity.Count);
        Assert.True(double.IsFinite(result.SeedingSharpe));
        Assert.True(double.IsFinite(result.SeedingAlphaAnn));

        using var db = h.Open();
        // The amputation, structurally: ZERO judging rows of ANY kind, no plants, and no status change.
        Assert.Empty(db.PowerReports.ToList());
        Assert.Empty(db.GoLiveLog.ToList());
        Assert.Empty(db.OverfittingStatus.ToList());
        Assert.Empty(db.OverfittingChecks.ToList());
        Assert.Empty(db.AllocationLog.ToList());
        Assert.DoesNotContain(db.Strategies.ToList(), s => s.StrategyId.StartsWith("plant:", StringComparison.Ordinal));
        Assert.Equal("candidate", db.Strategies.Single(s => s.StrategyId == "threshold:sma50").Status);

        // …and the track itself is quarantined replay rows.
        var account = db.Accounts.Single(a => a.StrategyId == "buyhold:ew" && a.RunKind == "replay");
        Assert.All(db.EquityCurve.Where(e => e.AccountId == account.AccountId).ToList(),
            e => Assert.Equal("replay", e.RunKind));

        // Determinism: the same request over a fresh generation reproduces the same summary.
        var again = await Engine(h).RunAsync(new BacktestRequest("buyhold:ew", h.Sessions[5], h.Sessions[35], Reset: true));
        Assert.Equal(result.SeedingSharpe, again.SeedingSharpe);
        Assert.Equal(result.SeedingAlphaAnn, again.SeedingAlphaAnn);
        Assert.Equal(result.Equity, again.Equity);
    }

    [Fact]
    public async Task FX_BacktestEngine_UnknownStrategy_FailsClosed()
    {
        using var h = new PipelineHarness();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine(h).RunAsync(new BacktestRequest("mom:unbuilt", h.Sessions[5], h.Sessions[10])));
        Assert.Contains("no registered model", ex.Message, StringComparison.Ordinal);
        using var db = h.Open();
        Assert.Empty(db.Runs.Where(r => r.RunKind == "replay").ToList());   // nothing ran
    }
}
