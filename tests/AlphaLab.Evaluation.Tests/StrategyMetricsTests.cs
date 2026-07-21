using AlphaLab.Evaluation.Metrics;

namespace AlphaLab.Evaluation.Tests;

public class StrategyMetricsTests
{
    [Fact]
    public void RiskFreeDaily_UsesThePlaceholderConstant_ByDefault()
    {
        // The RF placeholder is 0 until French RF ingestion lands; read in exactly one place.
        Assert.Equal(0.0, StrategyMetrics.RiskFreeDaily());
        Assert.Equal(MetricsConstants.RiskFreePlaceholderAnnual, 0.0);
    }

    [Fact]
    public void Returns_ComputesSimpleDailyReturns()
    {
        decimal[] equity = [100m, 110m, 99m];
        var r = StrategyMetrics.Returns(equity);
        Assert.Equal(2, r.Length);
        Assert.Equal(0.10, r[0], 10);
        Assert.Equal(-0.10, r[1], 10);
    }

    [Fact]
    public void JensenAlpha_RecoversConstructedAlphaAndBeta()
    {
        double[] bench = [0.01, -0.02, 0.03, 0.00, 0.015, -0.01, 0.02, -0.005];
        var strat = bench.Select(b => 0.0001 + 1.2 * b).ToArray();      // α=0.0001/day, β=1.2
        var a = StrategyMetrics.JensenAlpha(strat, bench, rfDaily: 0.0, lag: 2);
        Assert.Equal(1.2, a.Beta, 9);
        Assert.Equal(0.0001, a.AlphaDaily, 9);
        Assert.Equal(0.0001 * 252, a.AlphaAnnualized, 9);
    }

    [Fact]
    public void Sharpe_MatchesHandValue()
    {
        double[] r = [0.01, 0.03];                                       // mean 0.02, sample sd √0.0002
        // 0.02/√0.0002 · √252 = 1.4142136 · 15.874508 = 22.44994
        Assert.Equal(22.44994, StrategyMetrics.Sharpe(r, 0.0), 4);
    }

    [Fact]
    public void Sharpe_ConstantReturns_IsZero() =>
        Assert.Equal(0.0, StrategyMetrics.Sharpe([0.01, 0.01, 0.01], 0.0));

    [Fact]
    public void Sortino_MatchesHandValue()
    {
        double[] r = [0.02, -0.01];   // mean 0.005; downside dev √(0.0001/2)=0.00707107
        Assert.Equal(11.2249, StrategyMetrics.Sortino(r, 0.0), 3);
    }

    [Fact]
    public void InformationRatio_MatchesHandValue()
    {
        double[] strat = [0.03, 0.01];
        double[] bench = [0.01, 0.01];   // active [0.02, 0.00], mean 0.01, sd √0.0002
        Assert.Equal(11.2249, StrategyMetrics.InformationRatio(strat, bench), 3);
    }

    [Fact]
    public void MaxDrawdown_IsWorstPeakToTrough()
    {
        decimal[] equity = [100m, 120m, 90m, 110m];
        Assert.Equal(0.25, StrategyMetrics.MaxDrawdown(equity), 10);     // (120−90)/120
    }

    [Fact]
    public void AnnualizedReturn_DoublingOverAYear_Is100Pct()
    {
        var equity = new decimal[253];
        equity[0] = 100m;
        for (var i = 1; i < 252; i++) equity[i] = 150m;                  // interior points don't matter
        equity[252] = 200m;                                             // last/first = 2 over 252 steps
        Assert.Equal(1.0, StrategyMetrics.AnnualizedReturn(equity), 9);
    }

    [Fact]
    public void Calmar_IsReturnOverDrawdown_AndInfiniteWhenNoDrawdown()
    {
        Assert.Equal(2.0, StrategyMetrics.Calmar(0.2, 0.1), 10);
        Assert.Equal(double.PositiveInfinity, StrategyMetrics.Calmar(0.2, 0.0));
        Assert.Equal(0.0, StrategyMetrics.Calmar(-0.1, 0.0));
    }

    [Fact]
    public void Trades_ComputesExpectancyProfitFactorWinRate()
    {
        double[] pnls = [10, -5, 20, -5];
        var t = StrategyMetrics.Trades(pnls);
        Assert.Equal(4, t.N);
        Assert.Equal(5.0, t.Expectancy, 10);            // 20/4
        Assert.Equal(3.0, t.ProfitFactor, 10);          // 30 / 10
        Assert.Equal(0.5, t.WinRate, 10);
        Assert.Equal(15.0, t.AvgWin, 10);
        Assert.Equal(-5.0, t.AvgLoss, 10);
    }

    [Fact]
    public void Trades_AllWins_ProfitFactorIsInfinite()
    {
        var t = StrategyMetrics.Trades([1, 2, 3]);
        Assert.Equal(double.PositiveInfinity, t.ProfitFactor);
        Assert.Equal(1.0, t.WinRate);
    }

    [Fact]
    public void AnnualizedTurnover_OneWayScaledToYear()
    {
        // 50k traded on 100k book over a 63-day window ⇒ 0.5 × (252/63) = 2.0× annually.
        Assert.Equal(2.0, StrategyMetrics.AnnualizedTurnover(50_000, 100_000, 63), 10);
    }

    [Fact]
    public void DeflatedSharpe_NoTrials_IsUnchanged() =>
        Assert.Equal(0.6, StrategyMetrics.DeflatedSharpeAnnualized(0.6, 252, 1), 10);

    [Fact]
    public void DeflatedSharpe_ManyTrials_DeflatesBelowZero()
    {
        // A modest raw Sharpe (0.6 > the S2 raw>0.5 anchor) selected from 500 one-year trials is
        // indistinguishable from best-of-noise: the deflated statistic goes negative (the S2 elevated case).
        Assert.True(StrategyMetrics.DeflatedSharpeAnnualized(0.6, 252, 500) < 0);
    }

    [Fact]
    public void DeflatedSharpe_MonotonicallyDecreasesWithMoreTrials()
    {
        var few = StrategyMetrics.DeflatedSharpeAnnualized(1.5, 252, 5);
        var many = StrategyMetrics.DeflatedSharpeAnnualized(1.5, 252, 500);
        Assert.True(many < few);
    }
}
