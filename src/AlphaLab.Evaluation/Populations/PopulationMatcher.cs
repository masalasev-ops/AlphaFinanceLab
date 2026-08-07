using AlphaLab.Core.Domain;
using AlphaLab.Data;

namespace AlphaLab.Evaluation.Populations;

/// <summary>Which population is a given strategy's null, and under which cadence family — the pair,
/// because S3 needs BOTH: the members supply the percentile and the family selects the calibrated
/// trajectory the percentile is judged against. A null <see cref="PopulationId"/> means no population
/// exists for <see cref="Family"/>, which is the fail-closed outcome, never an error.</summary>
public sealed record PopulationMatch(string Family, long? PopulationId);

/// <summary>
/// Resolves a strategy to its matched control population (D36 / catalog §5.2 / hard rule 9).
///
/// **Why this type exists.** Until 6.3 the mapping was the literal string "daily" written at three
/// sites — `DailyPipeline`'s S3 comparator, its turnover-match family, and `BandInputs`' re-derivation
/// — and `BandInputs` carried a comment stating that its copy must stay in step with the pipeline's.
/// That is a relational constraint enforced by prose, which is the class of defect rule 25 exists to
/// stop: the comment even cited a line number that had already moved. One resolver removes the
/// possibility rather than documenting it.
///
/// **The cost-on predicate is not decoration.** Two `control_populations` rows carry the family token
/// "daily" — the cost-on band and its display-only cost-free twin, which share the name AND the seed.
/// Resolving by name alone would return either. Hard rule 9 and OVERFITTING_MONITOR §2 are explicit
/// that the cost-free population never serves as an S3 comparator, so `CostsOn` is the boundary.
///
/// **Fail closed, with the catalog's own words.** A strategy that declares a family with no spawned
/// population resolves to a null population id, and the monitor's existing `memberAlphas.Count == 0`
/// branch renders S3 `undefined`. That is catalog §5.2's prescription verbatim — *"until it exists, S3
/// is undefined for those strategies and the FX-TurnoverMatch cost-match caveat renders"* — not an
/// invention, and deliberately NOT a fall back to the daily null: judging a monthly strategy against a
/// daily null is the mismatch this checkpoint exists to remove, so doing it as an error path would
/// reintroduce it exactly where nobody would look.
///
/// **Known gap, recorded rather than papered over.** Only `Monitor.S3.PNoiseCurve.daily` and
/// `.PEdgeCurve.daily` have ever been frozen (generation 2 built curve sources from daily-family plant
/// cohorts). A strategy that resolves to `banded` or `monthly` therefore finds no trajectory and falls
/// to the flat pre-calibration anchors — which is CONSERVATIVE (D63/Change 3 made the flat path demand
/// a SUSTAINED dip, so it trips less readily, not more) but is still a weaker judgement. Per-family
/// curves must be frozen before a non-daily strategy registers; that is a Phase-4-calibration job, not
/// this checkpoint's, and it is recorded as such.
/// </summary>
public abstract class PopulationMatcher
{
    public abstract PopulationMatch For(string strategyId);

    /// <summary>
    /// One population for every strategy — the shape a test wants, and the shape the pipeline had
    /// hardcoded. Kept as an explicit, named constructor so "everything shares this null" is a visible
    /// claim a fixture is making rather than an implicit consequence of a `long?` parameter.
    /// </summary>
    public static PopulationMatcher Fixed(long? populationId, string family = CadenceFamily.Daily) =>
        new FixedMatcher(new PopulationMatch(family, populationId));

    /// <summary>
    /// The live rule: each strategy's DECLARED cadence family (D133's frozen bag), resolved to that
    /// family's cost-on population. Reads are cached per run — the monitor asks once per promotable
    /// strategy and the roster is small, but the population lookup is a table scan.
    /// </summary>
    public static PopulationMatcher ByDeclaration(AlphaLabDbContext db) => new DeclaredMatcher(db);

    private sealed class FixedMatcher(PopulationMatch match) : PopulationMatcher
    {
        public override PopulationMatch For(string strategyId) => match;
    }

    private sealed class DeclaredMatcher(AlphaLabDbContext db) : PopulationMatcher
    {
        private readonly Dictionary<string, PopulationMatch> _byStrategy = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long?> _byFamily = new(StringComparer.Ordinal);

        public override PopulationMatch For(string strategyId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
            if (_byStrategy.TryGetValue(strategyId, out var cached)) return cached;

            var row = db.Strategies.FirstOrDefault(s => s.StrategyId == strategyId);
            var declared = CadenceFamily.DeclaredIn(StrategyConfigJson.Read(row?.ConfigJson));

            // Absent ⇒ the compatibility family (the row predates the key and D17 forbids adding it).
            // Declared ⇒ used AS DECLARED, even if unknown to this build: an unknown token resolves to
            // no population and S3 goes undefined, which is a louder and more accurate outcome than
            // quietly substituting the default the strategy did not ask for.
            var family = declared ?? CadenceFamily.CompatibilityDefault;

            if (!_byFamily.TryGetValue(family, out var populationId))
            {
                populationId = db.ControlPopulations
                    .Where(p => p.Family == family && p.CostsOn)
                    .Select(p => (long?)p.PopulationId)
                    .FirstOrDefault();
                _byFamily[family] = populationId;
            }

            var match = new PopulationMatch(family, populationId);
            _byStrategy[strategyId] = match;
            return match;
        }
    }
}
