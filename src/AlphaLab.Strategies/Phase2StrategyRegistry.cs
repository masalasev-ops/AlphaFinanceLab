using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;

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
/// How the D53 pipeline (2.10) runs one account: the runnable <see cref="IModel"/>, the universe it is
/// handed, and the per-account run settings the model itself cannot carry.
///
/// The two run-settings seams were STATED in checkpoint 2.9 and are supplied here:
///  • CAP-WEIGHT needs <c>Sizing.PositionCapPct = 1.0</c> so a single name can be a full position;
///  • EQUAL-WEIGHT needs <c>Guardrails.MaxConcurrentPositions</c> ≥ the universe so it holds the whole
///    index, not a top-N slice.
/// Everything else stays at the bound config defaults (the funnel reads them; this only overrides what
/// each benchmark's shape structurally requires).
/// </summary>
public sealed record StrategyRunPlan(
    IModel Model,
    UniverseScope Universe,
    SizingOptions Sizing,
    GuardrailsOptions Guardrails);

/// <summary>
/// Maps a persisted <c>strategies.strategy_id</c> back to its runnable model + run plan for the D53
/// pipeline. The <c>strategies</c> table stores frozen config, not runnable code; this is the registry
/// that turns an account's strategy_id into something the funnel can execute.
///
/// Phase 2 knows exactly three strategies (the <see cref="DummyRoster"/> dummies). An account whose
/// strategy_id is not one of them resolves to null — the pipeline logs it and skips the account rather
/// than guessing a model (rule 10). The real strategies arrive in Phase 6, at which point this registry
/// grows a config-driven construction path; for the three trivial dummies a switch is the honest shape.
/// </summary>
public static class Phase2StrategyRegistry
{
    /// <summary>The run plan for a known strategy_id, or null if this build does not know how to run it.</summary>
    public static StrategyRunPlan? For(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        // Base the per-account settings on FRESH option instances (never a shared mutable one) so an
        // override on one benchmark can never bleed into another.
        return strategyId switch
        {
            "buyhold:cw" => new StrategyRunPlan(
                BuyAndHoldModel.CapWeight(),
                UniverseScope.CapWeightProxy,
                // 100% in the single proxy name (the benchmark IS the market, cap-weighted).
                new SizingOptions { PositionCapPct = 1.0 },
                new GuardrailsOptions()),

            "buyhold:ew" => new StrategyRunPlan(
                BuyAndHoldModel.EqualWeight(),
                UniverseScope.FullIndex,
                // Equal weight is breadth-controlled, so the per-position cap must not bind; the real
                // ceiling is MaxConcurrentPositions, set far above any single-index size below.
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
}
