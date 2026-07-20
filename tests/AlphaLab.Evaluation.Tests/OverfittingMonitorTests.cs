using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Monitor;

namespace AlphaLab.Evaluation.Tests;

public class MonitorSignalsTests
{
    [Theory]
    [InlineData(0.6, -0.2, true)]    // deflated < 0 while raw > 0.5 ⇒ elevated (pure selection)
    [InlineData(0.6, 0.1, false)]    // deflated still positive
    [InlineData(0.3, -0.2, false)]   // raw not above 0.5
    public void S2_ElevatedOnlyWhenDeflationFlipsAPositiveSharpe(double raw, double deflated, bool elevated)
    {
        var s = MonitorSignals.S2(raw, deflated);
        Assert.Equal(elevated ? "elevated" : "none", s.Contribution);
        Assert.Equal(elevated ? MonitorStatus.Warning : MonitorStatus.Healthy, s.Status);
    }

    [Theory]
    [InlineData(10.0, "suspect", MonitorStatus.Suspect)]    // < 25th — the anti-predictive tail (D63)
    [InlineData(50.0, "in_band", MonitorStatus.Healthy)]    // median no-edge — NEVER Suspect
    [InlineData(97.0, "healthy", MonitorStatus.Healthy)]    // ≥ 95th — distinguishable above
    [InlineData(25.0, "in_band", MonitorStatus.Healthy)]    // the boundary is not Suspect
    public void S3_FlatAnchors_OnlyTheAntiPredictiveTailIsSuspect(double pct, string contribution, MonitorStatus status)
    {
        var s = MonitorSignals.S3(pct);
        Assert.Equal(contribution, s.Contribution);
        Assert.Equal(status, s.Status);
    }

    [Fact]
    public void S6_IsCappedAtWarning_NeverSuspectOnASingleEvaluation()
    {
        // D63: a single 63-day window trips t < −1 ~16% of the time under the null, so S6 must not be a
        // single-eval Suspect (it would auto-retire honest no-edge controls). Both triggers ⇒ Warning.
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(rollingAlphaT: -1.5, insideCentralBand: true).Status);
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.S6(rollingAlphaT: 0.2, insideCentralBand: true).Status);
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.S6(rollingAlphaT: 0.2, insideCentralBand: false).Status);
    }

    [Fact]
    public void Aggregate_IsTheMaxSeverity_OverTheWhitelistedSignalsOnly()
    {
        Assert.Equal(MonitorStatus.Healthy, MonitorSignals.Aggregate([MonitorStatus.Healthy, MonitorStatus.Healthy, MonitorStatus.Healthy]));
        Assert.Equal(MonitorStatus.Suspect, MonitorSignals.Aggregate([MonitorStatus.Warning, MonitorStatus.Suspect, MonitorStatus.Healthy]));
        Assert.Equal(MonitorStatus.Warning, MonitorSignals.Aggregate([MonitorStatus.Warning, MonitorStatus.Healthy]));
    }
}

public class OverfittingMonitorTests
{
    private static double[] Centroid(IReadOnlyList<double[]> members, int len) =>
        Enumerable.Range(0, len).Select(t => members.Average(m => m[t])).ToArray();

    [Fact]
    public void FX_SyntheticEdge_LandsAbove95thPercentile_Healthy()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        // A strong constant excess above the population centroid ⇒ alpha far above every member.
        var edge = Centroid(members, 99).Select(r => r + 0.003).ToArray();
        arena.SeedStrategy("cand:edge", "candidate", dates, edge);

        using var db = arena.Open();
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:edge");

        Assert.True(result.S3.Value >= 95, $"edge S3 percentile was {result.S3.Value}");
        Assert.Equal(MonitorStatus.Healthy, result.S3.Status);
    }

    [Fact]
    public void FX_SyntheticNoEdge_SitsInBand_NeverSuspect()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        // The population centroid lands at the middle of the alpha distribution — a genuine no-edge draw.
        arena.SeedStrategy("cand:noedge", "candidate", dates, Centroid(members, 99));

        using var db = arena.Open();
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:noedge");

        Assert.True(result.S3.Value >= 25, $"no-edge S3 percentile {result.S3.Value} must not be in the Suspect tail (D63)");
        Assert.True(result.S3.Value < 95, $"no-edge S3 percentile {result.S3.Value} should be in-band, not distinguishable");
        Assert.NotEqual(MonitorStatus.Suspect, result.Status);
    }

    [Fact]
    public void AntiPredictive_LandsInTheSuspectTail()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));

        const int m = 60;
        var members = Enumerable.Range(0, m).Select(i => EvalArena.Noise(99, 0.01, 3000 + i)).ToList();
        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 1001, dates, i => members[i], m);

        // A constant NEGATIVE excess below the centroid ⇒ alpha below the 25th percentile.
        var anti = Centroid(members, 99).Select(r => r - 0.003).ToArray();
        arena.SeedStrategy("cand:anti", "candidate", dates, anti);

        using var db = arena.Open();
        var result = new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId).Single(r => r.StrategyId == "cand:anti");

        Assert.True(result.S3.Value < 25, $"anti-predictive S3 percentile was {result.S3.Value}");
        Assert.Equal(MonitorStatus.Suspect, result.S3.Status);
        Assert.Equal(MonitorStatus.Suspect, result.Status);
    }

    [Fact]
    public void Run_PersistsThreeCheckRowsPerStrategy_AndOneStatusRow()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(80, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(79, 0.008, seed: 5));
        arena.SeedStrategy("cand:a", "candidate", dates, EvalArena.Noise(79, 0.01, seed: 9));
        var popId = arena.SeedPopulation("daily", true, 1001, dates,
            i => EvalArena.Noise(79, 0.01, 3000 + i), m: 30);

        using var db = arena.Open();
        new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId);

        var checks = db.OverfittingChecks.Where(c => c.StrategyId == "cand:a").Select(c => c.Signal).OrderBy(s => s).ToList();
        Assert.Equal(["S2", "S3", "S6"], checks);
        Assert.Single(db.OverfittingStatus.Where(o => o.StrategyId == "cand:a"));
        Assert.All(db.OverfittingChecks.ToList(), c => Assert.Equal("live", c.RunKind));
    }
}
