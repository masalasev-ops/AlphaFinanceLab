using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Metrics;

/// <summary>The β-adjusted (Jensen's) alpha fit: slope, per-day + annualized intercept, and its
/// Newey–West t-stat (D48). Annualization of the intercept is arithmetic (×252).</summary>
public readonly record struct AlphaResult(double Beta, double AlphaDaily, double AlphaAnnualized, double AlphaTStat, int N, int Lag);

/// <summary>The per-trade evidence twin of alpha (§1.1 / D10): win rate is meaningless without the
/// average win/loss beside it, so they travel together.</summary>
public readonly record struct TradeStats(int N, double Expectancy, double ProfitFactor, double WinRate, double AvgWin, double AvgLoss);

/// <summary>
/// Pure per-strategy metrics (DESIGN_IMPROVEMENTS §1.1), net of costs by construction (the equity/trade
/// series handed in already carry D43 costs). All statistics are double (D69: derived statistics stay
/// REAL, never the ledger's decimal). No DB, no clock, no RNG — deterministic in the inputs.
/// </summary>
public static class StrategyMetrics
{
    private const double Ann = MetricsConstants.TradingDaysPerYear;

    /// <summary>The one place the RF placeholder is read (see <see cref="MetricsConstants"/>). Simple
    /// daily rate = annual / 252 (0 under the placeholder, so the compounding convention is moot).</summary>
    public static double RiskFreeDaily(double riskFreeAnnual = MetricsConstants.RiskFreePlaceholderAnnual) => riskFreeAnnual / Ann;

    /// <summary>Simple daily returns from an equity curve: r_t = e_t/e_{t−1} − 1. Skips any step whose
    /// prior equity is ≤ 0 (undefined) rather than producing a NaN/inf (rule 10).</summary>
    public static double[] Returns(IReadOnlyList<decimal> equity)
    {
        if (equity.Count < 2) return [];
        var r = new List<double>(equity.Count - 1);
        for (var i = 1; i < equity.Count; i++)
        {
            var prev = (double)equity[i - 1];
            if (prev <= 0.0) continue;
            r.Add((double)equity[i] / prev - 1.0);
        }
        return [.. r];
    }

    /// <summary>β-adjusted Jensen's alpha: OLS of (r_s − r_f) on (r_b − r_f) with NW HAC errors (lag
    /// L). Benchmark = the cap-weight account's returns. The intercept is α (per day); annualized ×252.</summary>
    public static AlphaResult JensenAlpha(IReadOnlyList<double> stratReturns, IReadOnlyList<double> benchReturns, double rfDaily, int lag)
    {
        if (stratReturns.Count != benchReturns.Count)
            throw new ArgumentException("Strategy and benchmark return series must be the same length.");

        var y = new double[stratReturns.Count];
        var x = new double[benchReturns.Count];
        for (var i = 0; i < y.Length; i++) { y[i] = stratReturns[i] - rfDaily; x[i] = benchReturns[i] - rfDaily; }

        var fit = NeweyWest.Ols(y, x, lag);
        return new AlphaResult(fit.Beta, fit.Alpha, fit.Alpha * Ann, fit.AlphaT, fit.N, fit.Lag);
    }

    /// <summary>Annualized Sharpe: mean(excess)/std(excess)·√252. Excess is over the daily RF.</summary>
    public static double Sharpe(IReadOnlyList<double> returns, double rfDaily)
    {
        if (returns.Count < 2) return 0.0;
        var (mean, sd) = MeanStd(returns, rfDaily);
        return sd > 0 ? mean / sd * Math.Sqrt(Ann) : 0.0;
    }

    /// <summary>Annualized Sortino: mean(excess)/downside-deviation·√252 (target 0; semideviation over
    /// all observations).</summary>
    public static double Sortino(IReadOnlyList<double> returns, double rfDaily)
    {
        if (returns.Count < 2) return 0.0;
        double mean = 0, dd = 0;
        foreach (var r in returns) mean += r - rfDaily;
        mean /= returns.Count;
        foreach (var r in returns)
        {
            var ex = r - rfDaily;
            if (ex < 0) dd += ex * ex;
        }
        dd = Math.Sqrt(dd / returns.Count);
        return dd > 0 ? mean / dd * Math.Sqrt(Ann) : 0.0;
    }

    /// <summary>Annualized information ratio: mean(active)/tracking-error·√252, active = r_s − r_b (D26).</summary>
    public static double InformationRatio(IReadOnlyList<double> stratReturns, IReadOnlyList<double> benchReturns)
    {
        if (stratReturns.Count != benchReturns.Count) throw new ArgumentException("Series must be the same length.");
        if (stratReturns.Count < 2) return 0.0;
        var active = new double[stratReturns.Count];
        for (var i = 0; i < active.Length; i++) active[i] = stratReturns[i] - benchReturns[i];
        var (mean, sd) = MeanStd(active, 0.0);
        return sd > 0 ? mean / sd * Math.Sqrt(Ann) : 0.0;
    }

    /// <summary>Worst peak-to-trough drawdown of an equity curve, as a positive fraction (0 = none).</summary>
    public static double MaxDrawdown(IReadOnlyList<decimal> equity)
    {
        if (equity.Count == 0) return 0.0;
        var peak = (double)equity[0];
        var maxDd = 0.0;
        foreach (var e in equity)
        {
            var v = (double)e;
            if (v > peak) peak = v;
            if (peak > 0)
            {
                var dd = (peak - v) / peak;
                if (dd > maxDd) maxDd = dd;
            }
        }
        return maxDd;
    }

    /// <summary>CAGR of an equity curve over its <c>Count−1</c> daily steps: (e_last/e_first)^(252/n)−1.</summary>
    public static double AnnualizedReturn(IReadOnlyList<decimal> equity)
    {
        if (equity.Count < 2) return 0.0;
        var first = (double)equity[0];
        var last = (double)equity[^1];
        if (first <= 0) return 0.0;
        var n = equity.Count - 1;
        return Math.Pow(last / first, Ann / n) - 1.0;
    }

    /// <summary>Calmar: annualized return ÷ max drawdown. +∞ when there is no drawdown but a positive
    /// return; 0 when flat.</summary>
    public static double Calmar(double annualizedReturn, double maxDrawdown)
    {
        if (maxDrawdown > 0) return annualizedReturn / maxDrawdown;
        return annualizedReturn > 0 ? double.PositiveInfinity : 0.0;
    }

    /// <summary>
    /// Deflated Sharpe (annualized) = raw − SR0·√252, where SR0 is the expected MAXIMUM per-period
    /// Sharpe under <paramref name="nTrials"/> independent null strategies of <paramref name="nObs"/>
    /// observations (Bailey–López de Prado haircut). With ≤ 1 trial there is no selection to deflate.
    /// The SIGN of the result is what S2 reads ("deflated &lt; 0 while raw &gt; 0.5").
    /// </summary>
    public static double DeflatedSharpeAnnualized(double rawSharpeAnnualized, int nObs, int nTrials)
    {
        if (nTrials <= 1 || nObs < 2) return rawSharpeAnnualized;
        const double euler = 0.5772156649015329;
        var varSr = 1.0 / nObs;                                   // per-period Sharpe estimate variance under the null
        var sr0PerPeriod = Math.Sqrt(varSr) *
            ((1.0 - euler) * Normal.InvCdf(1.0 - 1.0 / nTrials) +
             euler * Normal.InvCdf(1.0 - 1.0 / (nTrials * Math.E)));
        return rawSharpeAnnualized - sr0PerPeriod * Math.Sqrt(Ann);
    }

    /// <summary>Trade-level evidence (§1.1). <paramref name="tradePnls"/> are per-trade net P&amp;L
    /// (already costed). Profit factor = Σwins / |Σlosses| (+∞ with wins and no losses; 0 otherwise).</summary>
    public static TradeStats Trades(IReadOnlyList<double> tradePnls)
    {
        var n = tradePnls.Count;
        if (n == 0) return new TradeStats(0, 0, 0, 0, 0, 0);

        double sum = 0, wins = 0, losses = 0;
        int nWins = 0, nLosses = 0;
        foreach (var p in tradePnls)
        {
            sum += p;
            if (p > 0) { wins += p; nWins++; }
            else if (p < 0) { losses += p; nLosses++; }
        }

        var expectancy = sum / n;
        var winRate = (double)nWins / n;
        var avgWin = nWins > 0 ? wins / nWins : 0.0;
        var avgLoss = nLosses > 0 ? losses / nLosses : 0.0;      // negative or 0
        var profitFactor = losses < 0 ? wins / -losses : (wins > 0 ? double.PositiveInfinity : 0.0);
        return new TradeStats(n, expectancy, profitFactor, winRate, avgWin, avgLoss);
    }

    /// <summary>
    /// Annualized turnover — the cost model's lever (§1.1). One-way: Σ(buy notional) over the window ÷
    /// average equity, scaled to a year by 252/window-days. Consistent convention so a strategy and its
    /// matched population are comparable (finding 115).
    /// </summary>
    public static double AnnualizedTurnover(double buyNotional, double averageEquity, int windowDays)
    {
        if (averageEquity <= 0 || windowDays <= 0) return 0.0;
        return buyNotional / averageEquity * (Ann / windowDays);
    }

    private static (double Mean, double Std) MeanStd(IReadOnlyList<double> series, double offset)
    {
        var n = series.Count;
        double mean = 0;
        foreach (var v in series) mean += v - offset;
        mean /= n;
        double ss = 0;
        foreach (var v in series)
        {
            var d = v - offset - mean;
            ss += d * d;
        }
        var std = Math.Sqrt(ss / (n - 1));                        // sample std (Bessel)
        return (mean, std);
    }
}
