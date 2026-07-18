namespace AlphaLab.Core.Domain;

/// <summary>
/// A strategy's frozen parameters (D17), persisted to strategies.config_json.
///
/// FROZEN means frozen: hard rule 8 — any change to a live strategy forks a NEW strategy_id and
/// increments trials_registry. Never tune a live strategy against the monitor. This record is
/// immutable so a caller cannot mutate a loaded config in place and quietly change what a live
/// strategy does.
///
/// <see cref="Seed"/> is part of the determinism contract (catalog §2): a model must be
/// deterministic given (inputs, watermark, Config.Seed). NFR-1 / F-DET depend on it.
/// </summary>
public sealed record StrategyConfig
{
    /// <summary>The RNG seed. Required even for deterministic models so the contract is uniform
    /// and F-DET has one place to look.</summary>
    public required int Seed { get; init; }

    public required SelectionRule Selection { get; init; }

    public required SizingMode Sizing { get; init; }

    /// <summary>Per-strategy numeric parameters (lookback, skip, exitRank, …). CONFIG key rule 1:
    /// per-strategy parameters live here, NOT in appsettings — that file holds only system-level
    /// knobs. Kept as a bag so a fork's parameter set is data, not a schema change.</summary>
    public IReadOnlyDictionary<string, double> Params { get; init; } =
        new Dictionary<string, double>();

    /// <summary>D52 pre-registration escape hatch: true iff the strategy was created without a
    /// linked hypothesis. Rendered PERMANENTLY on the strategy card (hard rule 16). The
    /// CandidateFactory that enforces this arrives in Phase 3; the flag lives here from the start
    /// so a Phase-2 dummy is honestly marked rather than retro-fitted.</summary>
    public bool Unregistered { get; init; }

    public double Param(string name) =>
        Params.TryGetValue(name, out var v)
            ? v
            : throw new KeyNotFoundException(
                $"StrategyConfig has no parameter '{name}'. Parameters are frozen (D17) — " +
                "a missing one is a config error, never a default.");
}
