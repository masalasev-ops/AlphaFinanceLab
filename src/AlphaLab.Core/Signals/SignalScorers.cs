using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Signals;

/// <summary>
/// Shared arithmetic for the v1 scorers. Kept internal and tiny on purpose: every helper here is a
/// definition the fixtures hand-compute against (<c>FX-SignalScorers</c>), so a reader can check the
/// formula rather than trust it.
/// </summary>
internal static class ScorerMath
{
    /// <summary>Simple returns from an oldest-first price series: r_i = P_i/P_{i-1} − 1.</summary>
    internal static double[] DailyReturns(IReadOnlyList<double> series)
    {
        if (series.Count < 2) return [];
        var r = new double[series.Count - 1];
        for (var i = 1; i < series.Count; i++)
        {
            r[i - 1] = series[i - 1] > 0 ? series[i] / series[i - 1] - 1.0 : 0.0;
        }
        return r;
    }

    /// <summary>
    /// OLS slope of <paramref name="y"/> on <paramref name="x"/> — cov/var, the market beta.
    /// Null when the series disagree in length, are too short, or the market has no variance (a
    /// degenerate design has no beta, and inventing one would be the fail-OPEN direction).
    ///
    /// This is deliberately its OWN small computation rather than a call into the Evaluation metrics:
    /// Core cannot reference Evaluation (the CI reference graph), and — the substantive half — the
    /// beta adjustment belongs INSIDE the scorer, never through the `EvaluationStep` path that
    /// finding 285 indicts for feeding a raw active-return gap to the gate and allocator.
    /// </summary>
    internal static double? Beta(IReadOnlyList<double> y, IReadOnlyList<double> x)
    {
        if (y.Count != x.Count || y.Count < 2) return null;
        double my = 0, mx = 0;
        for (var i = 0; i < y.Count; i++) { my += y[i]; mx += x[i]; }
        my /= y.Count; mx /= x.Count;
        double cov = 0, varx = 0;
        for (var i = 0; i < y.Count; i++)
        {
            var dx = x[i] - mx;
            cov += (y[i] - my) * dx;
            varx += dx * dx;
        }
        return varx > 0 ? cov / varx : null;
    }

    /// <summary>Total simple return across an oldest-first series: P_last/P_first − 1.</summary>
    internal static double? TotalReturn(IReadOnlyList<double> series) =>
        series.Count >= 2 && series[0] > 0 ? series[^1] / series[0] - 1.0 : null;
}

/// <summary>
/// mom:L252s21 — classic momentum (Jegadeesh–Titman): the trailing 252-session return, SKIPPING the
/// most recent 21 sessions. The skip is the whole point of the rule: the last month carries the
/// short-term reversal effect, so including it contaminates the momentum signal with its opposite.
/// Score = P[t−21]/P[t−273] − 1. Needs 274 sessions; a name with fewer is omitted.
/// </summary>
public sealed class MomentumSkipSignal : ISignal
{
    private const int Lookback = 252;
    private const int Skip = 21;

    public string SignalId => "mom:L252s21";
    public string Family => "momentum";
    public string CodeVersion => SignalRegistry.CodeVersion;
    public IReadOnlyDictionary<string, double> Params { get; } =
        new Dictionary<string, double> { ["L"] = Lookback, ["skip"] = Skip };

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);

        var need = Lookback + Skip + 1;
        var scores = new Dictionary<SecurityId, double>();
        foreach (var id in eligible)
        {
            var series = features.AdjCloseSeries(id, need);
            if (series.Count < need || series[0] <= 0) continue;   // omit — absence is the answer
            var end = series[^(Skip + 1)];                          // P[t−21]
            scores[id] = end / series[0] - 1.0;
        }
        return scores;
    }
}

/// <summary>
/// mom:L126 — medium momentum: the trailing 126-session return, no skip (the catalog's example
/// family). Score = P[t]/P[t−126] − 1.
/// </summary>
public sealed class MediumMomentumSignal : ISignal
{
    private const int Lookback = 126;

    public string SignalId => "mom:L126";
    public string Family => "momentum";
    public string CodeVersion => SignalRegistry.CodeVersion;
    public IReadOnlyDictionary<string, double> Params { get; } =
        new Dictionary<string, double> { ["L"] = Lookback };

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);

        var scores = new Dictionary<SecurityId, double>();
        foreach (var id in eligible)
        {
            var series = features.AdjCloseSeries(id, Lookback + 1);
            if (series.Count < Lookback + 1) continue;
            if (ScorerMath.TotalReturn(series) is { } r) scores[id] = r;
        }
        return scores;
    }
}

/// <summary>
/// rev:L21 — short-term reversal (De Bondt–Thaler direction, short window): recent LOSERS rank HIGH,
/// so the score is the NEGATED trailing 21-session return. The sign is the rule, not a presentation
/// choice — a rank-IC on the un-negated return would measure momentum and report the reversal
/// hypothesis backwards.
/// </summary>
public sealed class ShortReversalSignal : ISignal
{
    private const int Lookback = 21;

    public string SignalId => "rev:L21";
    public string Family => "reversal";
    public string CodeVersion => SignalRegistry.CodeVersion;
    public IReadOnlyDictionary<string, double> Params { get; } =
        new Dictionary<string, double> { ["L"] = Lookback };

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);

        var scores = new Dictionary<SecurityId, double>();
        foreach (var id in eligible)
        {
            var series = features.AdjCloseSeries(id, Lookback + 1);
            if (series.Count < Lookback + 1) continue;
            if (ScorerMath.TotalReturn(series) is { } r) scores[id] = -r;   // losers rank high
        }
        return scores;
    }
}

/// <summary>
/// lowvol:L252 — low volatility: realized daily volatility over 252 sessions, INVERTED so that quiet
/// names rank high. Reuses <see cref="IFeatureView.RealizedVolDaily"/>, the same σ the D43 cost model
/// consumes, so the instrument grades the volatility definition the lab actually trades on.
/// </summary>
public sealed class LowVolSignal : ISignal
{
    private const int Window = 252;

    public string SignalId => "lowvol:L252";
    public string Family => "lowvol";
    public string CodeVersion => SignalRegistry.CodeVersion;
    public IReadOnlyDictionary<string, double> Params { get; } =
        new Dictionary<string, double> { ["L"] = Window };

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);

        var scores = new Dictionary<SecurityId, double>();
        foreach (var id in eligible)
        {
            if (features.RealizedVolDaily(id, Window) is { } vol) scores[id] = -vol;   // quiet ranks high
        }
        return scores;
    }
}

/// <summary>
/// brk:L252 — breakout strength: proximity to the trailing 252-session high, as P[t] / max(P over the
/// window). At the high the score is 1.0; well below it, less. Bounded and unit-free, so it ranks
/// sensibly across names of any price level.
/// </summary>
public sealed class BreakoutSignal : ISignal
{
    private const int Window = 252;

    public string SignalId => "brk:L252";
    public string Family => "breakout";
    public string CodeVersion => SignalRegistry.CodeVersion;
    public IReadOnlyDictionary<string, double> Params { get; } =
        new Dictionary<string, double> { ["L"] = Window };

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);

        var scores = new Dictionary<SecurityId, double>();
        foreach (var id in eligible)
        {
            var series = features.AdjCloseSeries(id, Window);
            if (series.Count < Window) continue;
            var high = series.Max();
            if (high > 0) scores[id] = series[^1] / high;
        }
        return scores;
    }
}

/// <summary>
/// resmom:L252 — residual momentum (Blitz direction): the trailing 252-session return with the market
/// component removed, i.e. r_stock − β·r_market over the window, β estimated on daily returns against
/// the market proxy across the same window.
///
/// THE BETA ADJUSTMENT HAPPENS HERE, INSIDE THE SCORER — never through `EvaluationStep`, whose raw
/// active-return gap is finding 285's defect. This is the correct place for it, and because a Phase-6
/// IModel wraps this same instance (finding 295), a beta adjustment correct here is correct on both
/// paths by construction.
///
/// No proxy resolved ⇒ no scores (fail closed, rule 10) rather than silently degrading to raw momentum,
/// which would report a different signal under this signal's name.
/// </summary>
public sealed class ResidualMomentumSignal : ISignal
{
    private const int Window = 252;

    public string SignalId => "resmom:L252";
    public string Family => "resmom";
    public string CodeVersion => SignalRegistry.CodeVersion;
    public IReadOnlyDictionary<string, double> Params { get; } =
        new Dictionary<string, double> { ["L"] = Window };

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(context);

        if (context.MarketProxy is not { } proxy) return new Dictionary<SecurityId, double>();
        var market = features.AdjCloseSeries(proxy, Window + 1);
        if (market.Count < Window + 1) return new Dictionary<SecurityId, double>();
        var marketReturns = ScorerMath.DailyReturns(market);
        var marketTotal = ScorerMath.TotalReturn(market);
        if (marketTotal is not { } rm) return new Dictionary<SecurityId, double>();

        var scores = new Dictionary<SecurityId, double>();
        foreach (var id in eligible)
        {
            if (id.Value == proxy.Value) continue;                       // the proxy does not grade itself
            var series = features.AdjCloseSeries(id, Window + 1);
            if (series.Count < Window + 1) continue;
            if (ScorerMath.TotalReturn(series) is not { } rs) continue;
            if (ScorerMath.Beta(ScorerMath.DailyReturns(series), marketReturns) is not { } beta) continue;
            scores[id] = rs - beta * rm;
        }
        return scores;
    }
}

/// <summary>
/// bab:L252 — betting against beta (Frazzini–Pedersen direction): the estimated market beta over 252
/// sessions, INVERTED so low-beta names rank high. Beta is estimated inside the scorer for the same
/// reason as <see cref="ResidualMomentumSignal"/>.
/// </summary>
public sealed class BettingAgainstBetaSignal : ISignal
{
    private const int Window = 252;

    public string SignalId => "bab:L252";
    public string Family => "bab";
    public string CodeVersion => SignalRegistry.CodeVersion;
    public IReadOnlyDictionary<string, double> Params { get; } =
        new Dictionary<string, double> { ["L"] = Window };

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(context);

        if (context.MarketProxy is not { } proxy) return new Dictionary<SecurityId, double>();
        var market = features.AdjCloseSeries(proxy, Window + 1);
        if (market.Count < Window + 1) return new Dictionary<SecurityId, double>();
        var marketReturns = ScorerMath.DailyReturns(market);

        var scores = new Dictionary<SecurityId, double>();
        foreach (var id in eligible)
        {
            if (id.Value == proxy.Value) continue;
            var series = features.AdjCloseSeries(id, Window + 1);
            if (series.Count < Window + 1) continue;
            if (ScorerMath.Beta(ScorerMath.DailyReturns(series), marketReturns) is { } beta)
            {
                scores[id] = -beta;   // low beta ranks high
            }
        }
        return scores;
    }
}
