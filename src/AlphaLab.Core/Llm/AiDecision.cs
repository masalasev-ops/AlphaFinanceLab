namespace AlphaLab.Core.Llm;

/// <summary>
/// The four D104 §23.8.1 artefacts for one AI decision — the record that makes re-execution unnecessary.
///
/// **The governing principle (§23.8):** every other component in this system earns its reproducibility by
/// RE-RUNNING. An LLM cannot, so the substitution is that the AI seat's **record must be complete enough
/// that re-execution is never required.** Each artefact catches a failure the others structurally cannot
/// see, which is why all four exist rather than the one that feels sufficient.
/// </summary>
/// <param name="PackHash">(a) Ties the decision to the exact stored pack bytes — catches **wrong input**:
/// the model saw something other than what the operator believes it saw.</param>
/// <param name="RawOutput">(b) The raw model output, verbatim, not only the parsed result — catches
/// **misparse**: the parser silently dropped, coerced or truncated part of a well-formed answer.</param>
/// <param name="AppliedJson">
/// (c) The parsed decision AND what the funnel actually did with it — catches **misapplication**.
///
/// **This is the artefact most easily lost**, because (a) and (b) together prove what was asked and
/// answered while neither shows what the arena *did*. Without it a correct decision and a correct log can
/// coexist with a wrong trade, and nothing in the record would show it: a guardrail rejection, a sizing
/// clamp or a cash constraint sitting between the decision and the fill is exactly the gap it closes.
/// Null until a funnel consumes the decision (Phase 6) — the field exists now so the seam is built WITH
/// it rather than around it.
/// </param>
/// <param name="ModelVersion">(d) The model that served the call — catches **behaviour change after a
/// model swap**: the same pack and prompt yielding different behaviour for a reason outside the pack.</param>
/// <param name="PromptVersion">(d) A frozen param (D81 rule 2); any change forks a candidate.</param>
/// <param name="SamplingJson">
/// (d), continued. Named for what the pinned tier actually HAS: it accepts no
/// <c>temperature</c>/<c>top_p</c>/<c>top_k</c>, so what there is to persist is the **effort and thinking
/// configuration**. Recording a "sampling parameters" field that is always empty would satisfy the letter
/// of the artefact while recording nothing — the artefact is satisfied by persisting what exists.
/// </param>
public sealed record AiDecisionRecord(
    string StrategyId,
    string AsOf,
    string PackHash,
    string PromptVersion,
    string ModelVersion,
    string RawOutput,
    TokenUsage Usage,
    string? AppliedJson = null,
    string? SamplingJson = null);

/// <summary>
/// The D81 persist-before-use seam.
///
/// **Rule 1: the persisted output IS the decision.** One call per (strategy, as-of); the response persists
/// here BEFORE use; the funnel consumes the stored row and any re-run replays it, never re-calling. That
/// is how a nondeterministic sampler satisfies NFR-1: determinism for AI-seated strategies reads
/// **f(inputs, watermark, seeds, stored AI outputs)** (§13.5).
///
/// <see cref="TryGetAsync"/> existing at all is the mechanism — a consumer that finds a stored row must
/// use it rather than calling the model, and `FX-AiDecisionIsTheRow` proves the provider seam sees **zero
/// API calls** on a re-run.
/// </summary>
public interface IAiDecisionStore
{
    /// <summary>Persist a decision BEFORE the funnel uses it. Append-only: a second write for the same
    /// (strategy, as_of, prompt_version) returns the stored row rather than overwriting, because the row
    /// is a record of what happened and a later call cannot revise it.</summary>
    Task<AiDecisionRecord> PersistAsync(AiDecisionRecord decision, CancellationToken ct = default);

    /// <summary>The stored decision, or null. A non-null result means **do not call the model**.</summary>
    Task<AiDecisionRecord?> TryGetAsync(
        string strategyId, string asOf, string promptVersion, CancellationToken ct = default);

    /// <summary>Record what the funnel did with a decision (artefact (c)), once it has done it.</summary>
    Task RecordAppliedAsync(
        string strategyId, string asOf, string promptVersion, string appliedJson, CancellationToken ct = default);
}
