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

    /// <summary>
    /// Frozen parameters that are NOT numbers (D133). <see cref="Params"/> is
    /// <c>IReadOnlyDictionary&lt;string,double&gt;</c>, so every frozen param the corpus assigns to
    /// <c>config_json</c> that is a STRING — instruction text, model id, a pack recipe version, a
    /// prompt hash, D127's `shortlist_recipe` id — had nowhere to live and would otherwise have been
    /// encoded as a magic double.
    ///
    /// ADDITIVE AND OPTIONAL (D133): absent from every row frozen before v1.9.95, and absent is not the
    /// same as empty — D17 forbids re-serializing over a frozen row, so an old row simply carries no
    /// entry here and must still read.
    /// </summary>
    public IReadOnlyDictionary<string, string> Frozen { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Frozen parameters that are ORDERED SETS (D133). D127's dispersion signal set is the first, and
    /// the reason the shape needs a list member at all: registering a new signal must never alter a
    /// running contestant's shortlist, which is only checkable if the set it was frozen with can be
    /// read back. Order is part of the frozen value, not incidental.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FrozenSets { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// The declared HOLDING-HORIZON shape (D133 closes this hole).
    ///
    /// <c>strategies.holding_horizon_days</c> stores only <see cref="HoldingHorizon.Days_"/>, so
    /// <c>ToRankExit</c> and <c>ToNextRebalance</c> BOTH persist as NULL and are indistinguishable when
    /// read back — the shape was not recoverable from the store at all. Serialized by its DECLARED type
    /// so the polymorphic <c>kind</c> discriminator is written (the <c>ExitPolicy</c> precedent), and
    /// nested under its own property so that discriminator cannot collide with <c>ExitPolicy</c>'s,
    /// which uses the same property name. Optional: absent on every row frozen before v1.9.95.
    /// </summary>
    public HoldingHorizon? Horizon { get; init; }

    /// <summary>The frozen string for <paramref name="name"/>. Fails closed like <see cref="Param"/>:
    /// a missing frozen param is a config error, never a default.</summary>
    public string FrozenValue(string name) =>
        Frozen.TryGetValue(name, out var v)
            ? v
            : throw new KeyNotFoundException(
                $"StrategyConfig has no frozen parameter '{name}'. Frozen params are immutable (D17) — " +
                "a missing one is a config error, never a default.");

    /// <summary>The frozen set for <paramref name="name"/>, order preserved. Fails closed.</summary>
    public IReadOnlyList<string> FrozenSet(string name) =>
        FrozenSets.TryGetValue(name, out var v)
            ? v
            : throw new KeyNotFoundException(
                $"StrategyConfig has no frozen set '{name}'. Frozen params are immutable (D17) — " +
                "a missing one is a config error, never a default.");

    public double Param(string name) =>
        Params.TryGetValue(name, out var v)
            ? v
            : throw new KeyNotFoundException(
                $"StrategyConfig has no parameter '{name}'. Parameters are frozen (D17) — " +
                "a missing one is a config error, never a default.");
}
