using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Data;

namespace AlphaLab.Evaluation.Ai;

/// <summary>What the researcher seat has spent this month, and whether a PAIR of arms still fits.</summary>
/// <param name="SpentUsd">Month-to-date spend attributable to the researcher seat.</param>
/// <param name="CapUsd">`Ai.Researcher.MonthlyBudgetUsd`.</param>
/// <param name="EstimatedPairUsd">The estimated cost of dispatching BOTH arms.</param>
/// <param name="PairFits">False ⇒ **neither** arm dispatches.</param>
public sealed record ResearcherBudgetState(
    decimal SpentUsd, decimal CapUsd, decimal EstimatedPairUsd, bool PairFits)
{
    public decimal RemainingUsd => Math.Max(0m, CapUsd - SpentUsd);
}

/// <summary>
/// The D82 researcher budget, read as a **pair** headroom check (D113).
///
/// **Why the pair and not the arm.** Both the treatment and the control draw on the same monthly budget.
/// If the budget were checked per arm, exhaustion between them would emit a treatment proposal with no
/// control — an unpaired observation silently entering the margin series, which is worse than no
/// observation because it looks like one. So the question this class answers is not "can the seat afford
/// a call" but "can it afford the pair", and the answer gates both arms together.
///
/// **Spend is attributed per seat from `analysis_cache`, not from `llm_budget_log`.** The budget log is
/// one row per DAY across every seat and task, so it cannot answer a per-seat question; the cache carries
/// the task and the cost of each call, and the researcher's tasks are exactly the three below. No schema
/// change, and the attribution is a read of what was actually spent rather than a parallel tally that
/// could drift from it.
/// </summary>
public sealed class ResearcherSeatBudget(AlphaLabDbContext db, AiOptions ai)
{
    /// <summary>The tasks whose spend belongs to the researcher seat (D79/D82).</summary>
    public static readonly IReadOnlyList<string> SeatTasks =
    [
        AnalysisTaskNames.Hypotheses,
        AnalysisTaskNames.ResearchBrief,
        AnalysisTaskNames.Skeptic,
    ];

    /// <summary>
    /// Assess headroom for a pair of arms in the month containing <paramref name="asOf"/>.
    ///
    /// <paramref name="estimatedArmUsd"/> is the conservative per-arm estimate the caller already has
    /// from the cost model. Conservative on purpose: an underestimate here is the thing that produces the
    /// unpaired observation this check exists to prevent.
    /// </summary>
    public ResearcherBudgetState Assess(string asOf, decimal estimatedArmUsd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var monthPrefix = asOf[..7];   // yyyy-MM
        var spent = (decimal)db.AnalysisCache
            .Where(r => SeatTasks.Contains(r.Task) && r.AsOf.StartsWith(monthPrefix) && r.CostUsd != null)
            .Sum(r => r.CostUsd!.Value);

        var pair = estimatedArmUsd * 2m;
        return new ResearcherBudgetState(
            spent, ai.Researcher.MonthlyBudgetUsd, pair,
            spent + pair <= ai.Researcher.MonthlyBudgetUsd);
    }
}
