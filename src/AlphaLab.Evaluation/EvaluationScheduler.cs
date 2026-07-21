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

    /// <summary>
    /// SELF-HEALING cadence: an evaluation is DUE whenever the number of full cadences elapsed exceeds the
    /// number of evaluations already completed. Unlike the exact-boundary <see cref="IsEvaluationDay"/>, this
    /// re-drives a MISSED cadence — the evaluation runs post-commit in its own transaction (outside the
    /// atomic daily write, to stay in the &lt;60s budget), so a crash there can leave the day committed 'ok'
    /// with its evaluation never run. A boundary check keyed to "session count is exactly a multiple" would
    /// then never re-fire, freezing the leaderboard/allocation for a whole cadence and under-counting the
    /// consecutive-Suspect streak that gates auto-retire. Comparing elapsed-cadences to completed-evaluations
    /// catches up on the very next launch instead.
    /// </summary>
    public bool IsEvaluationDue(int sessionsSinceInception, int evaluationsCompleted) =>
        gate.EvaluationCadenceDays > 0
        && sessionsSinceInception / gate.EvaluationCadenceDays > evaluationsCompleted;
}
