using AlphaLab.Core.Domain;

namespace AlphaLab.Strategies;

/// <summary>
/// A deliberately trivial trend-filter strategy (STRATEGY_CATALOG §5, the Phase-1–2 dummy set). Its
/// only job is to exercise the funnel, the ledger, corporate-action semantics, and the exit plumbing
/// with a strategy that ACTUALLY SELECTS a varying subset — unlike Buy&amp;Hold, which holds everything.
/// It is NOT a real strategy and carries no hypothesis, so it is registered <c>unregistered</c> (D52 /
/// rule 16) and rendered permanently as such.
///
/// THE SIGNAL: a name scores 1.0 when its adjusted close is above its own <c>lookback</c>-session SMA
/// (a simple "above trend" filter), and 0.0 otherwise. Scoring on ADJUSTED close is D30/§13.8 — signals
/// use adjusted, the ledger uses raw, never mixed. A 0.0 score is the honest way to say "not this one":
/// Stage 3's zero-score invariant drops it, so the wish list is exactly the above-trend names and the
/// rest is cash — no padding. A name with less than <c>lookback</c> history is OMITTED (absence is the
/// honest answer, catalog §2), never scored on a short window.
///
/// EXIT: ScheduledRebalance — the book is re-weighted to the current above-trend set on the cadence
/// and held between rebalances. (Falling off the wish list is never itself a sell, rule 7; only the
/// ExitPolicy closes.)
/// </summary>
public sealed class ThresholdModel : IModel
{
    /// <summary>The SMA window, in sessions (StrategyConfig.Params). Frozen (D17).</summary>
    public const string ParamLookback = "lookback";

    public string Id { get; }
    public StrategyConfig Config { get; }
    public HoldingHorizon Horizon { get; }
    public ExitPolicy Exits { get; }

    private ThresholdModel(string id, StrategyConfig config, HoldingHorizon horizon, ExitPolicy exits)
    {
        Id = id;
        Config = config;
        Horizon = horizon;
        Exits = exits;
    }

    /// <summary>
    /// Build the dummy. <paramref name="lookback"/> is the SMA window; <paramref name="rebalanceEveryNDays"/>
    /// the hold cadence; <paramref name="minScore"/>/<paramref name="maxConcurrent"/> the Stage-3 rule.
    /// </summary>
    public static ThresholdModel Create(
        int lookback = 50,
        int rebalanceEveryNDays = 21,
        double minScore = 0.60,
        int maxConcurrent = 60,
        int seed = 0)
    {
        if (lookback < 2)
            throw new ArgumentOutOfRangeException(nameof(lookback), lookback, "SMA lookback needs ≥ 2 sessions.");

        var config = new StrategyConfig
        {
            Seed = seed,
            Selection = SelectionRule.Threshold(minScore, maxConcurrent),
            Sizing = SizingMode.Equal,
            Params = new Dictionary<string, double> { [ParamLookback] = lookback },
            Unregistered = true, // a trivial dummy with no hypothesis — honestly flagged (rule 16 / D52)
        };
        return new ThresholdModel($"threshold:sma{lookback}", config, new HoldingHorizon.ToNextRebalance(),
            new ExitPolicy.ScheduledRebalance(rebalanceEveryNDays));
    }

    /// <summary>
    /// Score each eligible name 1.0 if its adjusted close &gt; its lookback-SMA, else 0.0; omit a name
    /// with too little history. Deterministic given (inputs, watermark) — it reads only the watermark-
    /// bounded feature view (F-DET / rule 4).
    /// </summary>
    public Task<IReadOnlyDictionary<SecurityId, double>> ScoreUniverseAsync(
        IReadOnlyList<SecurityId> eligible,
        DateOnly asOf,
        IFeatureView features,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(features);

        var lookback = (int)Config.Param(ParamLookback);
        var scores = new Dictionary<SecurityId, double>();

        foreach (var id in eligible)
        {
            var series = features.AdjCloseSeries(id, lookback);
            if (series.Count < lookback) continue; // omit — not enough history to define the SMA (honest)

            var sma = series.Average();
            var current = series[^1];
            // Above its own trend ⇒ bullish (1.0); at or below ⇒ 0.0, which the zero-score invariant drops.
            scores[id] = current > sma ? 1.0 : 0.0;
        }

        return Task.FromResult<IReadOnlyDictionary<SecurityId, double>>(scores);
    }
}
