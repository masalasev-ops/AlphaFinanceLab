using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Monitor;

namespace AlphaLab.Evaluation.Tests;

public class TurnoverMatchTests
{
    private static readonly double Tol = new PopulationsOptions().TurnoverMatchTolerancePct;   // 30.0

    [Fact]
    public void FX_TurnoverMatch_LowChurnStrategyVsBandedPopulation_FiresTheCaveat()
    {
        // A rank-hysteresis momentum dummy churns ~0.2×/yr; the banded population churns ~0.9×/yr.
        double[] bandedPopulation = [0.85, 0.90, 0.95, 0.88, 0.92];
        var result = TurnoverMatch.Check(strategyTurnover: 0.2, bandedPopulation, Tol);

        Assert.True(result.Caveat);
        Assert.True(result.StrategyTurnover < result.BandLo);
    }

    [Fact]
    public void FX_TurnoverMatch_ChurnMatchedSibling_DoesNotFireTheCaveat()
    {
        double[] bandedPopulation = [0.85, 0.90, 0.95, 0.88, 0.92];
        var result = TurnoverMatch.Check(strategyTurnover: 0.90, bandedPopulation, Tol);   // right at the median

        Assert.False(result.Caveat);
        Assert.InRange(result.StrategyTurnover, result.BandLo, result.BandHi);
    }

    [Fact]
    public void Check_HighChurnAboveTheBand_AlsoFiresTheCaveat()
    {
        double[] pop = [0.85, 0.90, 0.95];
        Assert.True(TurnoverMatch.Check(strategyTurnover: 2.0, pop, Tol).Caveat);   // far above the +30% band
    }

    [Fact]
    public void WriteCheck_PersistsATurnoverMatchRow_WithANeutralContribution()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();

        var caveat = TurnoverMatch.WriteCheck(db, "2026-03-10", "mom:lowchurn",
            strategyTurnover: 0.2, populationTurnovers: [0.85, 0.90, 0.95], tolerancePct: Tol);

        Assert.True(caveat);
        var row = Assert.Single(db.OverfittingChecks.Where(c => c.Signal == "turnover_match").ToList());
        Assert.Equal("caveat", row.Contribution);
        Assert.Equal(0.2, row.Value);
        Assert.Contains("population_median", row.ThresholdJson);
    }

    [Fact]
    public void FX_TurnoverMatch_StatusNeutral_TheCaveatRowNeverMovesTheOverfittingStatus()
    {
        // Two IDENTICAL strategies (same returns ⇒ same S2/S3/S6). One also carries a turnover_match
        // caveat row. Their monitor status must be byte-identical — the aggregate is over S2/S3/S6 only.
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, EvalArena.Noise(99, 0.008, seed: 5));
        var same = EvalArena.Noise(99, 0.01, seed: 42);
        arena.SeedStrategy("cand:a", "candidate", dates, same);
        arena.SeedStrategy("cand:b", "candidate", dates, same);   // identical returns
        var popId = arena.SeedPopulation("daily", true, 1001, dates, i => EvalArena.Noise(99, 0.01, 3000 + i), m: 30);

        using var db = arena.Open();

        // Pre-write a scary-looking turnover_match caveat for cand:a ONLY.
        TurnoverMatch.WriteCheck(db, dates[^1], "cand:a", strategyTurnover: 0.01, populationTurnovers: [0.9, 0.95, 1.0], tolerancePct: Tol);

        new OverfittingMonitor(db, new GateOptions()).Run(dates[^1], "buyhold:cw", popId);

        var statusA = db.OverfittingStatus.Single(o => o.StrategyId == "cand:a").Status;
        var statusB = db.OverfittingStatus.Single(o => o.StrategyId == "cand:b").Status;
        Assert.Equal(statusB, statusA);   // the caveat row on 'a' did not move its verdict
    }
}
