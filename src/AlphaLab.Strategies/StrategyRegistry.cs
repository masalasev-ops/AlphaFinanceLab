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
/// a trial, and never trade a day.
///
/// **HOW MUCH OF THAT IS TRUE TODAY, stated because the paragraph above used to end "what runs is now
/// derived from what was FROZEN" and that sentence was true of ZERO strategies (D152, finding 422).**
/// <see cref="Families"/> is empty until 6.11, and the three <see cref="RunSettingKeys"/> have no writer
/// anywhere in the repo or in any arena — so the row-driven branch below is unreachable in production
/// and the only strategies that run are the three resolved by id. The lifecycle PATH is closed; the
/// derivation is not yet exercised by anything. What D152 adds is the missing half for the ids that DO
/// run: the model is still built in C#, but it is now RECONCILED with the frozen row and refused if
/// they disagree, so "derived from what was frozen" is at least CHECKED against what was frozen.
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

        // THE THREE PHASE-2 DUMMIES ARE STILL BUILT FROM C#, AND ARE NOW CHECKED AGAINST THEIR FROZEN ROW
        // (D152, finding 422). The carve-out's stated reason used to be that these rows "do not carry the
        // run settings a row-driven family reads". That was THREE KEYS WIDE, not whole-row wide, and the
        // sentence read as the latter: the rows carry Selection (mode/N/MinScore/MaxConcurrent), Seed,
        // Sizing and Params — every parameter the funnel consumes — and lack only `position_cap_pct`,
        // `max_concurrent_positions` and `universe_scope`, the three keys checkpoint 6.2 invented for
        // itself and that nothing has ever written. So the model can and must be reconciled with the row.
        //
        // WHY A CHECK RATHER THAN CONSTRUCTION FROM THE ROW. The two checkpoint-2.9 run-setting seams
        // (cap-weight's 100% position cap, equal-weight's breadth ceiling) are genuinely NOT in the rows,
        // and D17 forbids re-serializing a frozen row to add them, so something must still supply them
        // from code. Building the MODEL from the row and the SETTINGS from code would leave exactly the
        // same unchecked join, one field narrower. Checking binds both halves at once.
        //
        // FAIL CLOSED, LOUDLY (rule 10). Hard rule 8 says a change to a live strategy forks a new
        // strategy_id; nothing enforced it, because DummyRoster.RegisterStrategy is idempotent, so an
        // edit to a Create(...) default would be written NOWHERE and executed EVERYWHERE — no fork, no
        // trials_registry row, no log line, and every day after it judged under parameters the store
        // does not record. A null return would report that as "unknown strategy" and skip the account,
        // which is the wrong sentence for a rule-8 breach; this throws, because the arena's evidence
        // base is what is at stake and the only way to reach it is for a developer to edit a default.
        if (LegacyDummy(row.StrategyId) is { } legacy)
        {
            var frozenConfig = StrategyConfigJson.Read(row.ConfigJson);
            if (frozenConfig is null) return null;   // unreadable frozen config ⇒ unknown (rule 10)

            var divergence = FirstDivergence(legacy.Model.Config, frozenConfig);
            if (divergence is not null)
            {
                throw new InvalidOperationException(
                    $"Strategy '{row.StrategyId}' EXECUTES a parameter its frozen row does not record: {divergence}. " +
                    "Hard rule 8: a change to a live strategy forks a new strategy_id and increments " +
                    "trials_registry — it never edits what a running strategy does. Fork it, or revert the code.");
            }

            return legacy;
        }

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
    /// The first field on which the model a build would EXECUTE disagrees with the row that was FROZEN,
    /// or null when they agree. Returns the field rather than a bool so the refusal names what moved.
    ///
    /// <para>Scope is deliberate: every field the funnel actually consumes off <see cref="StrategyConfig"/>
    /// — Selection (all four terms), Seed, Sizing, Params. <c>Unregistered</c> is excluded because it is a
    /// registration marker rather than a run parameter (rule 16), and Frozen/FrozenSets/Horizon because
    /// the pre-D133 dummy rows carry none and comparing them would refuse on absence rather than on
    /// change. Anything added to <see cref="StrategyConfig"/> that a funnel reads belongs here too.</para>
    /// </summary>
    private static string? FirstDivergence(StrategyConfig executes, StrategyConfig frozen)
    {
        if (executes.Seed != frozen.Seed) return $"seed executes {executes.Seed}, frozen {frozen.Seed}";
        if (executes.Sizing != frozen.Sizing) return $"sizing executes {executes.Sizing}, frozen {frozen.Sizing}";
        if (executes.Selection.Mode != frozen.Selection.Mode) return $"selection.mode executes {executes.Selection.Mode}, frozen {frozen.Selection.Mode}";
        if (executes.Selection.N != frozen.Selection.N) return $"selection.n executes {executes.Selection.N}, frozen {frozen.Selection.N}";
        if (executes.Selection.MaxConcurrent != frozen.Selection.MaxConcurrent) return $"selection.max_concurrent executes {executes.Selection.MaxConcurrent}, frozen {frozen.Selection.MaxConcurrent}";
        if (executes.Selection.MinScore != frozen.Selection.MinScore) return $"selection.min_score executes {executes.Selection.MinScore}, frozen {frozen.Selection.MinScore}";

        foreach (var key in executes.Params.Keys.Concat(frozen.Params.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var inCode = executes.Params.TryGetValue(key, out var a);
            var inRow = frozen.Params.TryGetValue(key, out var b);
            if (!inCode) return $"params['{key}'] frozen at {b}, absent from the executed model";
            if (!inRow) return $"params['{key}'] executes {a}, absent from the frozen row";
            if (a != b) return $"params['{key}'] executes {a}, frozen {b}";
        }

        return null;
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
