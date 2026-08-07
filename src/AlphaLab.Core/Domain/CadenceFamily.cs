namespace AlphaLab.Core.Domain;

/// <summary>
/// The CADENCE FAMILY vocabulary — which random control population is a strategy's null (D36; catalog
/// §5.2). Settled at Phase 6 checkpoint 6.3, because until then nothing in the repo answered the
/// question at all: `strategies.family` is the STRATEGY family (momentum|meanrev|lowvol), a different
/// namespace, and no column, config key or code path mapped a strategy to daily|banded|monthly.
/// The pipeline hardcoded the daily population for every promotable strategy and said so in a comment.
///
/// **DECLARED, NEVER DERIVED.** The family is a FROZEN PARAM in `config_json` (D133's string bag), read
/// through <see cref="DeclaredIn"/>. Deriving it from the exit policy was the obvious alternative and it
/// does not work: only <c>ScheduledRebalance</c> carries an interval, so <c>Never</c>,
/// <c>TargetOrTimeStop</c> and <c>ChannelExit</c> would have no answer — and catalog §6.4 resolves
/// Breakout by SPAWNING a new family (`RandomPop-Event`), which no derivation rule can express. A
/// heuristic that is silently wrong for three of five exit shapes is worse than a declaration.
///
/// Being frozen is the point rather than a side effect: the Phase-6 rails already list "the population
/// family" among the params a change to which is a FORK under rule 8. A strategy is judged against the
/// null it was ADMITTED against, and moving that null later would re-score its whole history against a
/// different distribution.
///
/// **The vocabulary is not open.** These four are the SCHEMA `control_populations.family` tokens.
/// `event` (RandomPop-Event, catalog §6.4/§6.6) is deliberately ABSENT: it has no family seed in
/// `PopulationsOptions`, no SCHEMA comment and no spawned population, and it arrives with Breakout at
/// checkpoint 6.13. Naming it here before it can be resolved would let a strategy declare a family that
/// silently matches nothing.
/// </summary>
public static class CadenceFamily
{
    /// <summary>The `config_json` frozen key a strategy declares its matched population family under.</summary>
    public const string FrozenKey = "population_family";

    /// <summary>Re-draws every session — the null for mean-reversion / daily-churn families (§5.2).</summary>
    public const string Daily = "daily";

    /// <summary>Re-draws on momentum's rank-buffer band cadence (§5.2).</summary>
    public const string Banded = "banded";

    /// <summary>Re-draws at the monthly rebalance — low-vol's null (§5.2).</summary>
    public const string Monthly = "monthly";

    /// <summary>DORMANT until a Phase-8 quarterly strategy exists; its seed is reserved, its population
    /// is never spawned speculatively (§5.2). Declaring it today resolves to no population, which is
    /// the fail-closed outcome, not an error.</summary>
    public const string Quarterly = "quarterly";

    /// <summary>
    /// The family used for a strategy whose frozen row carries NO declaration.
    ///
    /// This is a compatibility rule with a recorded reason, not a silent default. Every row frozen
    /// before v1.9.96 — the three Phase-2 dummies and the D64 plants — predates the key, and D17
    /// forbids re-serializing over a frozen row to add it. The same carve-out `StrategyRegistry` makes
    /// for the legacy dummies, made for the same reason, and it preserves the frozen generation exactly:
    /// those rows were judged against the daily population, and they still are.
    ///
    /// It is deliberately NOT a fallback for a strategy that declares an unresolvable family — that case
    /// fails closed (see <c>PopulationMatcher</c>). Absent and wrong are different answers.
    /// </summary>
    public const string CompatibilityDefault = Daily;

    /// <summary>Every family that can be resolved today, in SCHEMA's token order.</summary>
    public static readonly IReadOnlyList<string> All = [Daily, Banded, Monthly, Quarterly];

    /// <summary>
    /// The family <paramref name="config"/> declares, or null when it declares none. Null is the honest
    /// answer for a pre-6.3 row and is what selects the compatibility rule; the caller distinguishes it
    /// from a declared-but-unspawned family, which must not resolve to anything.
    /// </summary>
    public static string? DeclaredIn(StrategyConfig? config) =>
        config is not null && config.Frozen.TryGetValue(FrozenKey, out var family)
            && !string.IsNullOrWhiteSpace(family)
                ? family
                : null;

    /// <summary>True when <paramref name="family"/> is a token this build knows how to resolve.</summary>
    public static bool IsKnown(string? family) =>
        family is not null && All.Contains(family, StringComparer.Ordinal);
}
