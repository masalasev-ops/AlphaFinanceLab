namespace AlphaLab.Worker.Pipeline;

/// <summary>
/// Stage 3 of the D53 staged pipeline: work that runs **after** the day's atomic write transaction has
/// committed, in its own transaction (rule 12; MASTER §20.4; FR-29).
///
/// **Why the boundary exists at all.** The invariant is one atomic write transaction per trading day
/// (golden rule 16). If the LLM stage ran inside it, a slow or failed model call would roll back a day's
/// committed trades — the arena's state would become hostage to a vendor's latency. Post-commit inverts
/// that: **a late or failed batch is a no-read day and nothing else.** Forward-only (D16) is what makes
/// that safe rather than merely tolerable, because the read would only ever have informed *subsequent*
/// days, so its absence costs nothing already committed.
///
/// The corollary binds any future implementer: **Stage 3 must never write anything Stage 2 reads.** If it
/// did, "its own transaction" would be a fiction — the day's state would depend on whether the batch came
/// back, which is precisely the coupling the boundary removes.
///
/// **Structural absence, not a flag.** The FORWARD composition registers the LLM stage; the REPLAY
/// composition registers nothing, so a replay cannot reach a model even by mistake — the shape
/// `FR21_Replay_HasNoAnalysisPath` prefers (compile-time absence over a runtime guard). Catch-up is
/// handled the same way at the call site: past days never run Stage 3.
/// </summary>
public interface IPostCommitStage
{
    /// <summary>Runs after the Stage-2 commit, outside its transaction. **A throw here must never fail
    /// the day** — the day is already committed and correct. Implementations return normally on any
    /// vendor failure; the pipeline defends against a rogue throw regardless.</summary>
    Task RunAsync(PipelineDayContext context, CancellationToken ct = default);
}
