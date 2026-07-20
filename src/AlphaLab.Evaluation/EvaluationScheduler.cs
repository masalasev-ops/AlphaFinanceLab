using AlphaLab.Core.Config;

namespace AlphaLab.Evaluation;

/// <summary>
/// Decides whether a session is an evaluation day (D31 — the 21-day cadence). Deterministic from the
/// SESSION COUNT since arena inception, never wall-clock (risk R-5: a wall-clock cadence drifts across
/// weekends/holidays and a zero-run arena has no clock anchor). Pure — the Worker supplies the count.
/// </summary>
public sealed class EvaluationScheduler(GateOptions gate)
{
    /// <summary>True on every <c>EvaluationCadenceDays</c>-th session (21, 42, 63, …). Session 0
    /// (inception) is never an evaluation day.</summary>
    public bool IsEvaluationDay(int sessionsSinceInception) =>
        sessionsSinceInception > 0 && sessionsSinceInception % gate.EvaluationCadenceDays == 0;
}
