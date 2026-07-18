using AlphaLab.Core.Domain;

namespace AlphaLab.Strategies;

/// <summary>
/// The two permanent Buy&amp;Hold benchmarks (STRATEGY_CATALOG §5.1). They are the honesty bar: the
/// cap-weight account is the default Jensen's-alpha regression benchmark (D26) and the equal-weight
/// account is displayed beside it (D27, construction-matched to the random populations and low-vol).
/// Neither is promotable.
///
/// ONE MODEL, TWO SHAPES. Buy&amp;Hold is indifferent across names — it wants to hold the whole
/// universe it is handed — so <see cref="ScoreUniverseAsync"/> scores every eligible name at 1.0 in
/// both shapes. What differs is (a) the universe the pipeline hands each account and (b) the exit:
///
///  • CAP-WEIGHT (<see cref="CapWeight"/>) holds ONE security — the cap-weight ETF proxy (OEF.US on the
///    S&amp;P 100 slice, IVV.US after the widening; the pipeline resolves it via
///    <see cref="CapWeightProxy"/> and hands this account a single-name roster). ExitPolicy = Never:
///    it enters once and never trades again (catalog §5.1, "allocate ~100% on first run; hold; never
///    re-score"). Scoring 1.0 every day is a no-op because the proxy is already held and Never never
///    closes — the funnel emits no order once it is in.
///  • EQUAL-WEIGHT (<see cref="EqualWeight"/>) is self-built: equal weight across the WHOLE eligible
///    universe, rebalanced monthly (D68) — never an EW ETF, which would embed a fund's own rebalance
///    timing and expense drag. ExitPolicy = ScheduledRebalance so Stage 4 re-weights the book on the
///    cadence and holds between rebalances.
///
/// Both are Sizing = Equal (the only executable mode in Phase 2; FR-11 partial). The cap-weight
/// account still needs the pipeline to run it with a per-position cap of 100% (Sizing.PositionCapPct
/// = 1.0) so a single name can be a full position; and the equal-weight account needs a breadth
/// guardrail (Guardrails.MaxConcurrentPositions) at least as large as the universe — both are
/// per-account run settings the D53 pipeline (2.10) supplies, not model concerns.
/// </summary>
public sealed class BuyAndHoldModel : IModel
{
    public string Id { get; }
    public StrategyConfig Config { get; }
    public HoldingHorizon Horizon => new HoldingHorizon.ToNextRebalance();
    public ExitPolicy Exits { get; }

    private BuyAndHoldModel(string id, ExitPolicy exits, StrategyConfig config)
    {
        Id = id;
        Exits = exits;
        Config = config;
    }

    /// <summary>Cap-weight benchmark: holds the single cap-weight ETF proxy, never trades again.
    /// The pipeline hands this account a one-name roster (the resolved proxy security_id).</summary>
    public static BuyAndHoldModel CapWeight(int seed = 0) => new(
        id: "buyhold:cw",
        exits: new ExitPolicy.Never(),
        // TopN(1): the account's universe is the single proxy, so one name is selected. MinScore's
        // default (0.60) is passed by the 1.0 score. Equal sizing of one name is 100% (under a 100% cap).
        config: new StrategyConfig
        {
            Seed = seed,
            Selection = SelectionRule.TopN(1),
            Sizing = SizingMode.Equal,
        });

    /// <summary>Equal-weight benchmark (D68): equal weight across the whole eligible universe,
    /// rebalanced every <paramref name="rebalanceEveryNDays"/> sessions (default 21 ≈ monthly).</summary>
    public static BuyAndHoldModel EqualWeight(int rebalanceEveryNDays = 21, int seed = 0) => new(
        id: "buyhold:ew",
        exits: new ExitPolicy.ScheduledRebalance(rebalanceEveryNDays),
        // TopN with a breadth far above any single-index size: the model wants EVERY eligible name.
        // Guardrails.MaxConcurrentPositions (a per-account run setting) is the real ceiling — for this
        // benchmark the pipeline sets it ≥ the universe so the whole index is held, not a top slice.
        config: new StrategyConfig
        {
            Seed = seed,
            Selection = SelectionRule.TopN(100_000),
            Sizing = SizingMode.Equal,
        });

    /// <summary>
    /// Score every eligible name at 1.0 — Buy&amp;Hold is maximally, equally bullish on the whole
    /// universe it is given (catalog §5.1). It reads NO features: it does not re-rank, and it never
    /// needs to, because holding is the entire strategy. Deterministic by construction (F-DET): the
    /// output depends only on the eligible set, not on any data or clock.
    /// </summary>
    public Task<IReadOnlyDictionary<SecurityId, double>> ScoreUniverseAsync(
        IReadOnlyList<SecurityId> eligible,
        DateOnly asOf,
        IFeatureView features,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        var scores = new Dictionary<SecurityId, double>(eligible.Count);
        foreach (var id in eligible) scores[id] = 1.0;
        return Task.FromResult<IReadOnlyDictionary<SecurityId, double>>(scores);
    }
}
