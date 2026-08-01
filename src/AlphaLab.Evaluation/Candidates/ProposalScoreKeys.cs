using AlphaLab.Data;
using AlphaLab.Data.Services;

namespace AlphaLab.Evaluation.Candidates;

/// <summary>
/// The two D110 proposal-quality parameters, and the check that they are pinned before the first proposal.
///
/// **They are versioned `config` rows, not appsettings, for two reasons that both bite later.** They are
/// score *inputs*, so a mid-experiment change breaks the proposal-to-proposal comparability D110's chained
/// criterion depends on — and an appsettings value is not as-of resolvable (the D106 `GateOptions` limit),
/// so a later recomputation could not reproduce the score a proposal was originally given. Same shape and
/// the same reasoning as D108's two trend alphas.
///
/// **Pinned BEFORE the first proposal exists**, which is why the check lives on the endpoint rather than
/// in the scorer: a parameter chosen after the first scores are visible is a parameter chosen by looking
/// at the answer. The scorer does not exist yet; the proposals it will read start accumulating now.
/// </summary>
public static class ProposalScoreKeys
{
    /// <summary>
    /// The clamp applied to a stated prior before the log scoring rule sees it.
    ///
    /// A log rule is unbounded at 0 and 1: an unclamped prior of exactly 1.0 on a refuted claim scores
    /// −∞ and destroys every aggregate it enters. The clamp is what keeps one over-confident proposal
    /// from being unrecoverable — a bound on the penalty, not a correction to the researcher.
    /// </summary>
    public const string PriorClamp = "Kpi.ProposalPriorClamp";

    /// <summary>
    /// The minimum number of CLOSED proposals before a calibration-skill figure is published at all.
    ///
    /// The leave-one-out base rate is estimated from the closed set, so below this count the reference
    /// point is noise and the "skill" measured against it is noise about noise. Publishing an
    /// `insufficient` verdict is the honest output; publishing a number is not.
    /// </summary>
    public const string ScoreMinClosed = "Kpi.ProposalScoreMinClosed";

    public static readonly IReadOnlyList<string> All = [PriorClamp, ScoreMinClosed];

    /// <summary>
    /// Which of the two are NOT yet pinned. Empty ⇒ proposals may be accepted.
    ///
    /// Returns the missing keys rather than a bool so the refusal can NAME them: "some threshold is
    /// missing" sends the operator looking, and the operator's next act is a `config` write that has to
    /// name the key anyway.
    /// </summary>
    public static IReadOnlyList<string> Unpinned(AlphaLabDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var reader = new ConfigReadService(db);
        return [.. All.Where(k => reader.ResolveCurrent(k) is null)];
    }
}
