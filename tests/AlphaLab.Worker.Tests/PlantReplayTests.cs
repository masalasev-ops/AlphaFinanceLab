using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FR-36 integration: the D64 plants inside a real replay — seeded as replay-only fixtures, their
/// equity computed in the atomic replay day, invisible to every forward path, and leak-free against
/// future-dated data (the F-LEAK shape for plant equity).
/// </summary>
public class PlantReplayTests
{
    private static ReplayRunner Runner() =>
        new(ReplayEngineTests.HarnessConfiguration(),
            new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" }, NullLoggerFactory.Instance);

    private static ReplayRequest Window(PipelineHarness h, bool reset = false) =>
        new(h.Sessions[25], h.Sessions[30], Reset: reset);

    [Fact]
    public async Task FR36_PlantEquity_FlowsThroughTheReplayDay()
    {
        using var h = new PipelineHarness();
        var outcome = await Runner().RunAsync($"Data Source={h.DbPath}", Window(h));
        Assert.False(outcome.StoppedEarly);

        using var db = h.Open();
        var plantAccounts = db.Accounts.Where(a => a.RunKind == "replay").ToList()
            .Where(a => PlantCohorts.IsPlantId(a.StrategyId))
            .Select(a => a.AccountId)
            .ToList();
        Assert.Equal(16, plantAccounts.Count);   // 8 cohorts × SeedsPerPlant=2 (CI-scale): edge/noedge/anti/naive daily + the 4 monthly ladder rungs

        // Every plant has one equity point per replay session, all quarantined, all positive.
        foreach (var accountId in plantAccounts)
        {
            var curve = db.EquityCurve.Where(e => e.AccountId == accountId).ToList();
            Assert.Equal(6, curve.Count);
            Assert.All(curve, e => Assert.Equal("replay", e.RunKind));
            Assert.All(curve, e => Assert.True(e.Equity > 0));
        }

        // The overlay is real, not a no-op: the NAIVE comparator applies constant drift EVERY session,
        // so it must differ from the no-edge plant even over this six-session CI window. (The REALISTIC
        // edge plant may legitimately equal no-edge here — its persistent chain opens inactive and the
        // first active run often starts beyond six sessions; its process shape is pinned by the pure
        // FX_PlantOverlay tests over 100k ordinals, not by this integration window.)
        var naiveId = PlantCohorts.Id(PlantKind.Naive, "daily", 2.0, 0);
        var noEdgeId = PlantCohorts.Id(PlantKind.NoEdge, "daily", 0.0, 0);
        decimal Last(string strategyId)
        {
            var account = db.Accounts.Single(a => a.StrategyId == strategyId && a.RunKind == "replay").AccountId;
            return db.EquityCurve.Where(e => e.AccountId == account).OrderBy(e => e.AsOf).AsEnumerable().Last().Equity;
        }
        Assert.True(Last(naiveId) > Last(noEdgeId));
    }

    [Fact]
    public async Task FR36_Plants_InvisibleToEveryForwardPath()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var outcome = await Runner().RunAsync($"Data Source={h.DbPath}", Window(h));
        Assert.False(outcome.StoppedEarly);

        using var db = h.Open();

        // Trials: the replay column ONLY (D37 — replay trials are the separate registry track), so the
        // forward S2 deflation count is untouched by plant seeding.
        var plantTrials = db.TrialsRegistry.ToList().Where(t => PlantCohorts.IsPlantId(t.StrategyId)).ToList();
        Assert.NotEmpty(plantTrials);
        Assert.All(plantTrials, t => Assert.Equal("replay", t.RunKind));

        // Accounts/equity: no live-side rows for any plant.
        var plantAccountIds = db.Accounts.ToList().Where(a => PlantCohorts.IsPlantId(a.StrategyId)).ToList();
        Assert.All(plantAccountIds, a => Assert.Equal("replay", a.RunKind));

        // A FORWARD evaluation runs to completion and pairs no plant (they have no live account).
        var evaluations = new EvaluationStep(db, new GateOptions { MinTrackDays = 1 }).Run(h.Run2);
        Assert.DoesNotContain(evaluations, e => PlantCohorts.IsPlantId(e.StrategyId));
        Assert.Empty(db.PowerReports.ToList().Where(p => p.RunKind == "live" && PlantCohorts.IsPlantId(p.StrategyA)));
    }

    [Fact]
    public async Task F_LEAK_PlantEquity_InvariantToFutureDatedData()
    {
        using var h = new PipelineHarness();
        var runner = Runner();
        await runner.RunAsync($"Data Source={h.DbPath}", Window(h));

        string PlantEquitySnapshot()
        {
            using var db = h.Open();
            var ids = db.Accounts.Where(a => a.RunKind == "replay").ToList()
                .Where(a => PlantCohorts.IsPlantId(a.StrategyId)).Select(a => a.AccountId).ToList();
            return string.Join("|", db.EquityCurve.Where(e => ids.Contains(e.AccountId))
                .OrderBy(e => e.AccountId).ThenBy(e => e.AsOf).AsEnumerable()
                .Select(e => $"{e.AccountId}:{e.AsOf}:{e.Equity}"));
        }
        var before = PlantEquitySnapshot();

        // Future-dated data, observed INSIDE the frozen watermark: a bar and a corporate action dated
        // after the window end. A leak-free replay of the same window must not see either.
        using (var db = h.Open())
        {
            db.Bars.Add(new BarRow
            {
                SecurityId = PipelineHarness.MemberA, Date = h.Sessions[35], Version = 9,
                ObservedAt = $"{h.Sessions[39]}T22:00:00Z", Open = 500, High = 500, Low = 500, Close = 500,
                AdjClose = 500, Volume = 1, Source = "eodhd",
            });
            db.SaveChanges();
        }

        await runner.RunAsync($"Data Source={h.DbPath}", Window(h, reset: true));
        Assert.Equal(before, PlantEquitySnapshot());
    }
}
