using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Calibration;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// WHICH STRATEGIES A FORWARD READ-MODEL MAY SHOW — one definition (D149).
///
/// Hard rule 1 quarantines replay from every forward view, and D64 plants are replay-only fixtures
/// (FR-36): they exist solely inside the quarantined generation, so no forward screen ever lists one.
/// That rule was previously hand-rolled at each call site, and one site forgot it —
/// <c>StrategiesReadModelBuilder.BuildDetail</c> served a plant as a forward strategy card while its
/// sibling <c>Build</c>, EIGHT LINES ABOVE, carried the filter with the rule cited in its own comment.
///
/// THE FIX IS THE SEAM, NOT THE INSTANCE. A predicate copied per call site fails by SILENT OMISSION —
/// nothing breaks, a screen simply shows a row it should not — and the next builder forgets it the same
/// way. Routing every forward reader through here means a new one has to go out of its way to be wrong.
///
/// THE INVERSE IS DELIBERATE AND SEPARATE. <c>ReplayReadModelBuilder</c> SELECTS plants, because the
/// replay screen's whole subject is the quarantined generation. It is not a violation of this rule; it is
/// the other side of it, and <see cref="IsReplayFixture"/> exists so that side is named rather than
/// re-expressed as a raw prefix test.
/// </summary>
internal static class ForwardVisibility
{
    /// <summary>True when this strategy id may appear on a FORWARD screen.</summary>
    public static bool IsForwardVisible(string strategyId) => !PlantCohorts.IsPlantId(strategyId);

    /// <summary>True when this strategy id is a replay-only calibration fixture — the inverse, named so
    /// the replay screen's deliberate selection reads as a decision rather than a stray prefix test.</summary>
    public static bool IsReplayFixture(string strategyId) => PlantCohorts.IsPlantId(strategyId);

    /// <summary>The forward-visible strategy rows, in id order. The ONE place a forward read-model gets
    /// its subject set; enumerated client-side because <see cref="IsForwardVisible"/> is not translatable
    /// to SQL, exactly as the hand-rolled copies did.</summary>
    public static IEnumerable<StrategyRow> ForwardStrategies(IQueryable<StrategyRow> strategies) =>
        strategies.OrderBy(s => s.StrategyId).AsEnumerable().Where(s => IsForwardVisible(s.StrategyId));
}
