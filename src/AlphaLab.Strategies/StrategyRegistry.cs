using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Data;
using AlphaLab.Data.Entities;

namespace AlphaLab.Strategies;

/// <summary>Which universe the D53 pipeline hands an account's funnel each day.</summary>
public enum UniverseScope
{
    /// <summary>The whole eligible index roster (equal-weight benchmark, threshold candidate).</summary>
    FullIndex,

    /// <summary>The single cap-weight ETF proxy security (the CW benchmark holds one name).</summary>
    CapWeightProxy,
}

/// <summary>
/// How the D53 pipeline runs one account: the runnable <see cref="IModel"/>, the universe it is handed,
/// and the per-account run settings the model itself cannot carry.
///
/// The two run-settings seams stated at checkpoint 2.9 are supplied by the registry:
///  • CAP-WEIGHT needs <c>Sizing.PositionCapPct = 1.0</c> so a single name can be a full position;
///  • EQUAL-WEIGHT needs <c>Guardrails.MaxConcurrentPositions</c> ≥ the universe so it holds the whole
///    index, not a top-N slice.
/// Everything else stays at the bound config defaults.
/// </summary>
public sealed record StrategyRunPlan(
    IModel Model,
    UniverseScope Universe,
    SizingOptions Sizing,
    GuardrailsOptions Guardrails);

/// <summary>
/// Turns a PERSISTED strategy row into something the D53 funnel can execute (Phase 6 checkpoint 6.2,
/// replacing Phase 2's three-arm id switch).
///
/// **Row-driven, not id-driven, and that is the point.** The `strategies` table stores frozen config,
/// and until D133 nothing could read it back — so the only way to run a strategy was to recognise its
/// id in a hardcoded switch, which is why an admitted candidate could pass the detectability gate, spend
/// a trial, and never trade a day. What runs is now derived from what was FROZEN.
///
/// **ONE entry point for both consumers.** `DailyPipeline` and `SeedingBacktestEngine` resolve through
/// the same call, so the daily run and the backtest engine can never accept different strategy sets —
/// a divergence there would let a strategy trade forward that replay refuses to seed, or the reverse.
///
/// **Unknown still means unknown (rule 10).** A row whose family has no registered factory resolves to
/// null and the caller skips it with a logged reason rather than guessing a model. That behaviour is
/// pinned by `RunDay_UnknownStrategyAccount_Skipped_RunStillCommits` and is deliberately preserved.
/// </summary>
public static class StrategyRegistry
{
    /// <summary>Config keys a row-driven family reads for its run plan (D133's typed shape).</summary>
    public static class RunSettingKeys
    {
        public const string PositionCapPct = "position_cap_pct";
        public const string MaxConcurrentPositions = "max_concurrent_positions";
        public const string UniverseScope = "universe_scope";
    }

    /// <summary>
    /// Family → how to build the runnable model from its frozen config. **Empty until the real families
    /// land at 6.11** (Momentum, MeanReversion, LowVol, …): this is the seam they register into, and
    /// leaving it empty is honest — Phase 6.2 closes the lifecycle path, it does not invent strategies.
    /// </summary>
    private static readonly Dictionary<string, Func<StrategyConfig, IModel>> Families =
        new(StringComparer.Ordinal);

    /// <summary>The run plan for a persisted strategy id, or null if this build cannot run it.</summary>
    public static StrategyRunPlan? For(AlphaLabDbContext db, string strategyId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        var row = db.Strategies.FirstOrDefault(s => s.StrategyId == strategyId);
        return row is null ? null : ForRow(row);
    }

    /// <summary>
    /// Can this build run <paramref name="strategyId"/> AT ALL — asked BEFORE a run that may itself
    /// create the row?
    ///
    /// The distinction from <see cref="For"/> is the ordering, and it is not a loosening. A seeding
    /// backtest resolves this before spending the replay, but the replay is also what seeds the legacy
    /// roster — so demanding a persisted row here would refuse the three dummies for not yet existing.
    /// A legacy dummy is answerable from its id alone; every row-driven family still needs a readable
    /// row, and a genuinely unknown id still fails fast rather than fabricating a track.
    /// </summary>
    public static bool CanRun(AlphaLabDbContext db, string strategyId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        return LegacyDummy(strategyId) is not null || For(db, strategyId) is not null;
    }

    /// <summary>The run plan for a row already in hand.</summary>
    public static StrategyRunPlan? ForRow(StrategyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The three Phase-2 dummies are resolved by id, with a recorded reason: their rows were frozen
        // BEFORE D133 made config_json readable, so they do not carry the run settings a row-driven
        // family reads — and D17 forbids re-serializing over a frozen row to add them. They are a
        // closed set that will never grow; every strategy admitted from here is row-driven.
        if (LegacyDummy(row.StrategyId) is { } legacy) return legacy;

        if (!Families.TryGetValue(row.Family, out var build)) return null;

        var config = StrategyConfigJson.Read(row.ConfigJson);
        if (config is null) return null;   // unreadable frozen config ⇒ unknown, never a guess (rule 10)

        // FRESH option instances per account (the Phase-2 discipline, kept): a shared mutable options
        // object would let an override on one strategy bleed into another's sizing or guardrails.
        var sizing = new SizingOptions();
        if (config.Params.TryGetValue(RunSettingKeys.PositionCapPct, out var cap)) sizing.PositionCapPct = cap;

        var guardrails = new GuardrailsOptions();
        if (config.Params.TryGetValue(RunSettingKeys.MaxConcurrentPositions, out var maxPos))
        {
            guardrails.MaxConcurrentPositions = (int)maxPos;
        }

        var scope = config.Frozen.TryGetValue(RunSettingKeys.UniverseScope, out var s)
                    && string.Equals(s, "cap_weight_proxy", StringComparison.Ordinal)
            ? UniverseScope.CapWeightProxy
            : UniverseScope.FullIndex;

        return new StrategyRunPlan(build(config), scope, sizing, guardrails);
    }

    /// <summary>
    /// The three Phase-2 dummies, unchanged from the switch they replace — including the two run-setting
    /// seams stated at checkpoint 2.9: cap-weight needs a 100% position cap so a single name can be a
    /// full position, and equal-weight needs a concurrent-position ceiling above the universe so it
    /// holds the whole index rather than a top-N slice.
    /// </summary>
    private static StrategyRunPlan? LegacyDummy(string strategyId) => strategyId switch
    {
        "buyhold:cw" => new StrategyRunPlan(
            BuyAndHoldModel.CapWeight(),
            UniverseScope.CapWeightProxy,
            new SizingOptions { PositionCapPct = 1.0 },
            new GuardrailsOptions()),

        "buyhold:ew" => new StrategyRunPlan(
            BuyAndHoldModel.EqualWeight(),
            UniverseScope.FullIndex,
            new SizingOptions { PositionCapPct = 1.0 },
            new GuardrailsOptions { MaxConcurrentPositions = 100_000 }),

        "threshold:sma50" => new StrategyRunPlan(
            ThresholdModel.Create(),
            UniverseScope.FullIndex,
            new SizingOptions(),
            new GuardrailsOptions()),

        _ => null,
    };
}
